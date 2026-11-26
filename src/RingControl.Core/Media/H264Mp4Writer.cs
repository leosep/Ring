using System.Buffers.Binary;
using System.Text;

namespace RingControl.Core.Media;

/// <summary>
/// Writes H.264 RTP payloads directly to a fragmented MP4 (ISO/IEC 14496-12) file.
///
/// The output is self-contained: ftyp + moov with an AVC decoder configuration record,
/// followed by one moof/mdat pair per second of video. Fragmented MP4 is crash-safe
/// (each fragment is independent) and plays in VLC, ffmpeg, browsers and most players,
/// which lets the NVR run on Linux without ffmpeg or LibVLC.
/// </summary>
public sealed class H264Mp4Writer : IDisposable
{
    private const int Timescale = 90000;
    private const int DefaultFrameDuration = Timescale / 15;

    private readonly object _sync = new();
    private readonly FileStream _file;
    private readonly int _fallbackWidth;
    private readonly int _fallbackHeight;

    private readonly List<byte[]> _pendingNals = new();
    private readonly List<Sample> _bufferedSamples = new();
    private readonly List<Sample> _fragmentSamples = new();

    private byte[]? _sps;
    private byte[]? _pps;
    private bool _headerWritten;
    private bool _pendingStarted;
    private uint _pendingTimestamp;
    private bool _pendingHasVcl;

    private Sample? _heldSample;
    private uint _heldTimestamp;
    private long _decodeTime;
    private long _fragmentDuration;
    private uint _sequence;
    private int _width;
    private int _height;
    private bool _disposed;

    private bool _hasFirstTimestamp;
    private uint _firstTimestamp;
    private uint _lastTimestamp;
    private long _frameCount;
    private int _lastDuration = DefaultFrameDuration;

    public H264Mp4Writer(string path, int fallbackWidth = 1280, int fallbackHeight = 720)
    {
        FilePath = path;
        _fallbackWidth = fallbackWidth > 0 ? fallbackWidth : 1280;
        _fallbackHeight = fallbackHeight > 0 ? fallbackHeight : 720;
        _width = _fallbackWidth;
        _height = _fallbackHeight;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024, useAsync: false);
        StartedAt = DateTimeOffset.Now;
    }

    public string FilePath { get; }

    public DateTimeOffset StartedAt { get; }

    public double FramesPerSecond { get; private set; } = 15;

    public int Width => _width;

    public int Height => _height;

    public int FrameCount { get; private set; }

    public long BytesWritten { get; private set; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_decodeTime / Timescale);

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return !_disposed;
            }
        }
    }

    /// <summary>Called for every H.264 RTP payload (RFC 6184). Thread-safe.</summary>
    public void WritePayload(ReadOnlySpan<byte> payload, uint rtpTimestamp)
    {
        if (payload.Length < 1)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            TrackFrame(rtpTimestamp);
            ParsePayload(payload, rtpTimestamp);

            if (!_headerWritten && _sps is not null && _pps is not null)
            {
                WriteHeader();
            }
        }
    }

    private void ParsePayload(ReadOnlySpan<byte> payload, uint timestamp)
    {
        var nalType = payload[0] & 0x1F;

        switch (nalType)
        {
            case 24: // STAP-A
            {
                var index = 1;
                while (index + 2 <= payload.Length)
                {
                    var length = (payload[index] << 8) | payload[index + 1];
                    index += 2;
                    if (length <= 0 || index + length > payload.Length)
                    {
                        break;
                    }

                    AddNal(payload.Slice(index, length), timestamp);
                    index += length;
                }

                break;
            }
            case 28: // FU-A
            {
                if (payload.Length < 2)
                {
                    break;
                }

                var fuHeader = payload[1];
                var isStart = (fuHeader & 0x80) != 0;
                var isEnd = (fuHeader & 0x40) != 0;
                var reconstructedHeader = (byte)((payload[0] & 0xE0) | (fuHeader & 0x1F));

                if (isStart)
                {
                    _fragment?.Dispose();
                    _fragment = new MemoryStream(payload.Length + 32);
                    _fragment.WriteByte(reconstructedHeader);
                }

                if (_fragment is not null)
                {
                    _fragment.Write(payload[2..]);
                }

                if (isEnd && _fragment is not null)
                {
                    AddNal(_fragment.ToArray(), timestamp);
                    _fragment.Dispose();
                    _fragment = null;
                }

                break;
            }
            case >= 1 and <= 23:
                AddNal(payload, timestamp);
                break;
        }
    }

    private MemoryStream? _fragment;

    private void AddNal(ReadOnlySpan<byte> nal, uint timestamp)
    {
        if (nal.Length == 0)
        {
            return;
        }

        var type = nal[0] & 0x1F;

        switch (type)
        {
            case 7:
                _sps = nal.ToArray();
                return;
            case 8:
                _pps = nal.ToArray();
                return;
            case 9: // access unit delimiter
            case 12: // filler data
                return;
        }

        var isVcl = type is >= 1 and <= 5;

        if (_pendingStarted && timestamp != _pendingTimestamp)
        {
            EmitAccessUnit();
        }

        if (!_pendingStarted)
        {
            _pendingStarted = true;
            _pendingTimestamp = timestamp;
            _pendingHasVcl = false;
        }

        _pendingNals.Add(nal.ToArray());
        if (isVcl)
        {
            _pendingHasVcl = true;
        }
    }

    private void EmitAccessUnit()
    {
        if (!_pendingStarted)
        {
            return;
        }

        var nals = _pendingNals.ToArray();
        var hasVcl = _pendingHasVcl;
        var timestamp = _pendingTimestamp;

        _pendingNals.Clear();
        _pendingStarted = false;
        _pendingHasVcl = false;

        if (!hasVcl || nals.Length == 0)
        {
            return;
        }

        var data = BuildSample(nals);
        var key = nals.Any(n => (n[0] & 0x1F) == 5);

        if (_heldSample is { } held)
        {
            var delta = (int)(timestamp - _heldTimestamp);
            if (delta <= 0 || delta > Timescale * 2)
            {
                delta = _lastDuration;
            }

            _lastDuration = delta;
            AppendSample(held with { Duration = delta });
        }

        _heldSample = new Sample(data, 0, key);
        _heldTimestamp = timestamp;
        FrameCount++;
    }

    private static byte[] BuildSample(byte[][] nals)
    {
        var size = 0;
        foreach (var nal in nals)
        {
            size += 4 + nal.Length;
        }

        var buffer = new byte[size];
        var offset = 0;
        foreach (var nal in nals)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), (uint)nal.Length);
            offset += 4;
            nal.CopyTo(buffer, offset);
            offset += nal.Length;
        }

        return buffer;
    }

    private void AppendSample(Sample sample)
    {
        if (!_headerWritten)
        {
            _bufferedSamples.Add(sample);
            if (_bufferedSamples.Count > 300)
            {
                _bufferedSamples.RemoveAt(0);
            }

            return;
        }

        _fragmentSamples.Add(sample);
        _fragmentDuration += sample.Duration;

        if (_fragmentDuration >= Timescale || _fragmentSamples.Count >= 60)
        {
            FlushFragment();
        }
    }

    private void WriteHeader()
    {
        if (_headerWritten || _sps is null || _pps is null)
        {
            return;
        }

        var (width, height) = SpsParser.ParseDimensions(_sps);
        if (width > 0 && height > 0)
        {
            _width = width;
            _height = height;
        }

        Write(Box("ftyp",
            Ascii("isom"),
            U32(512),
            Ascii("isom"),
            Ascii("iso6"),
            Ascii("mp41"),
            Ascii("avc1")));

        Write(BuildMoov());

        _headerWritten = true;

        foreach (var sample in _bufferedSamples)
        {
            _fragmentSamples.Add(sample);
            _fragmentDuration += sample.Duration;
        }

        _bufferedSamples.Clear();

        if (_fragmentSamples.Count > 0)
        {
            FlushFragment();
        }
    }

    private byte[] BuildMoov()
    {
        var matrix = Matrix();

        var mvhd = FullBox("mvhd", 0, 0, Concat(
            U32(0), U32(0), U32(1000), U32(0),
            U32(0x00010000), U16(0x0100), U16(0), U64(0),
            matrix,
            new byte[24],
            U32(2)));

        var tkhd = FullBox("tkhd", 0, 0x000007, Concat(
            U32(0), U32(0), U32(1), U32(0), U32(0),
            U64(0),
            U16(0), U16(0), U16(0), U16(0),
            matrix,
            U32((uint)_width << 16),
            U32((uint)_height << 16)));

        var mdhd = FullBox("mdhd", 0, 0, Concat(
            U32(0), U32(0), U32(Timescale), U32(0),
            U16(0x55C4), U16(0)));

        var hdlr = FullBox("hdlr", 0, 0, Concat(
            U32(0), Ascii("vide"), new byte[12], Ascii("VideoHandler\0")));

        var vmhd = FullBox("vmhd", 0, 1, Concat(U16(0), U16(0), U16(0), U16(0)));

        var dinf = Box("dinf", FullBox("dref", 0, 0, Concat(U32(1), FullBox("url ", 0, 1, Array.Empty<byte>()))));

        var avcC = Box("avcC", BuildAvcC());
        var avc1 = Box("avc1", BuildAvc1(avcC));

        var stbl = Box("stbl",
            FullBox("stsd", 0, 0, Concat(U32(1), avc1)),
            FullBox("stts", 0, 0, U32(0)),
            FullBox("stsc", 0, 0, U32(0)),
            FullBox("stsz", 0, 0, Concat(U32(0), U32(0))),
            FullBox("stco", 0, 0, U32(0)));

        var minf = Box("minf", vmhd, dinf, stbl);
        var mdia = Box("mdia", mdhd, hdlr, minf);
        var trak = Box("trak", tkhd, mdia);
        var trex = FullBox("trex", 0, 0, Concat(U32(1), U32(1), U32(0), U32(0), U32(0)));

        return Box("moov", mvhd, trak, Box("mvex", trex));
    }

    private byte[] BuildAvc1(byte[] avcC)
    {
        var compressor = new byte[32];
        Ascii("RingControl").CopyTo(compressor, 0);

        return Concat(
            new byte[6],
            U16(1),
            U16(0), U16(0),
            new byte[12],
            U16((ushort)_width), U16((ushort)_height),
            U32(0x00480000), U32(0x00480000),
            U32(0),
            U16(1),
            compressor,
            U16(0x0018),
            U16(0xFFFF),
            avcC);
    }

    private byte[] BuildAvcC()
    {
        var sps = _sps!;
        var pps = _pps!;
        var buffer = new byte[11 + sps.Length + pps.Length];
        var offset = 0;
        buffer[offset++] = 1;
        buffer[offset++] = sps.Length > 1 ? sps[1] : (byte)0x42;
        buffer[offset++] = sps.Length > 2 ? sps[2] : (byte)0x00;
        buffer[offset++] = sps.Length > 3 ? sps[3] : (byte)0x1F;
        buffer[offset++] = 0xFF;
        buffer[offset++] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)sps.Length);
        offset += 2;
        sps.CopyTo(buffer, offset);
        offset += sps.Length;
        buffer[offset++] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)pps.Length);
        offset += 2;
        pps.CopyTo(buffer, offset);
        return buffer;
    }

    private void FlushFragment()
    {
        if (_fragmentSamples.Count == 0)
        {
            return;
        }

        var baseTime = _decodeTime;
        var samples = _fragmentSamples.ToArray();

        var moofSize = BuildMoof(samples, baseTime, 0).Length;
        var dataOffset = moofSize + 8;
        var moof = BuildMoof(samples, baseTime, dataOffset);

        Write(moof);

        var payloadSize = samples.Sum(s => s.Data.Length);
        Write(U32((uint)(8 + payloadSize)));
        Write(Ascii("mdat"));
        foreach (var sample in samples)
        {
            Write(sample.Data);
        }

        _decodeTime += _fragmentDuration;
        _fragmentSamples.Clear();
        _fragmentDuration = 0;
        _sequence++;
        _file.Flush();
    }

    private byte[] BuildMoof(Sample[] samples, long baseTime, int dataOffset)
    {
        var mfhd = FullBox("mfhd", 0, 0, U32(_sequence + 1));
        var tfhd = FullBox("tfhd", 0, 0x020000, U32(1));
        var tfdt = FullBox("tfdt", 1, 0, U64((ulong)baseTime));

        var trunBody = new byte[8 + samples.Length * 12];
        BinaryPrimitives.WriteUInt32BigEndian(trunBody.AsSpan(0, 4), (uint)samples.Length);
        BinaryPrimitives.WriteInt32BigEndian(trunBody.AsSpan(4, 4), dataOffset);

        var offset = 8;
        foreach (var sample in samples)
        {
            BinaryPrimitives.WriteUInt32BigEndian(trunBody.AsSpan(offset, 4), (uint)sample.Duration);
            BinaryPrimitives.WriteUInt32BigEndian(trunBody.AsSpan(offset + 4, 4), (uint)sample.Data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(trunBody.AsSpan(offset + 8, 4), sample.Key ? 0x02000000u : 0x01010000u);
            offset += 12;
        }

        var trun = FullBox("trun", 0, 0x000701, trunBody);
        return Box("moof", mfhd, Box("traf", tfhd, tfdt, trun));
    }

    private void TrackFrame(uint timestamp)
    {
        if (!_hasFirstTimestamp)
        {
            _hasFirstTimestamp = true;
            _firstTimestamp = timestamp;
            _lastTimestamp = timestamp;
            _frameCount = 1;
            return;
        }

        if (timestamp == _lastTimestamp)
        {
            return;
        }

        _lastTimestamp = timestamp;
        _frameCount++;

        var span = _lastTimestamp - _firstTimestamp;
        if (span > 0 && _frameCount > 2)
        {
            var fps = (_frameCount - 1) * 90000.0 / span;
            if (fps is > 1 and < 61)
            {
                FramesPerSecond = Math.Round(fps, 2);
            }
        }
    }

    /// <summary>Flushes the pending access unit and closes the file.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            EmitAccessUnit();

            if (_heldSample is { } held)
            {
                AppendSample(held with { Duration = _lastDuration });
                _heldSample = null;
            }

            FlushFragment();

            _disposed = true;

            try
            {
                _fragment?.Dispose();
                _file.Flush();
                _file.Dispose();
            }
            catch
            {
                // Never throw while closing a recording.
            }
        }
    }

    public void Dispose() => Complete();

    private void Write(byte[] bytes)
    {
        _file.Write(bytes);
        BytesWritten += bytes.Length;
    }

    private readonly record struct Sample(byte[] Data, int Duration, bool Key);

    private static byte[] Matrix()
    {
        var matrix = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(matrix.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(matrix.AsSpan(16, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(matrix.AsSpan(32, 4), 0x40000000);
        return matrix;
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        var size = 8;
        foreach (var child in children)
        {
            size += child.Length;
        }

        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)size);
        Ascii(type).CopyTo(buffer, 4);

        var offset = 8;
        foreach (var child in children)
        {
            child.CopyTo(buffer, offset);
            offset += child.Length;
        }

        return buffer;
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
    {
        var buffer = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)buffer.Length);
        Ascii(type).CopyTo(buffer, 4);
        buffer[8] = version;
        buffer[9] = (byte)(flags >> 16);
        buffer[10] = (byte)(flags >> 8);
        buffer[11] = (byte)flags;
        payload.CopyTo(buffer, 12);
        return buffer;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var size = 0;
        foreach (var part in parts)
        {
            size += part.Length;
        }

        var buffer = new byte[size];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] U16(ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U64(ulong value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        return buffer;
    }
}

/// <summary>Minimal SPS parser used only to read the coded picture size.</summary>
internal static class SpsParser
{
    public static (int Width, int Height) ParseDimensions(byte[] sps)
    {
        try
        {
            var rbsp = RemoveEmulationPrevention(sps);
            var reader = new BitReader(rbsp);

            reader.ReadBits(8); // nal header
            var profile = reader.ReadBits(8);
            reader.ReadBits(8); // constraint flags
            reader.ReadBits(8); // level
            reader.ReadUnsignedExpGolomb(); // seq_parameter_set_id

            var chromaFormat = 1;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormat = (int)reader.ReadUnsignedExpGolomb();
                if (chromaFormat == 3)
                {
                    reader.ReadBits(1); // separate_colour_plane_flag
                }

                reader.ReadUnsignedExpGolomb(); // bit_depth_luma_minus8
                reader.ReadUnsignedExpGolomb(); // bit_depth_chroma_minus8
                reader.ReadBits(1); // qpprime_y_zero_transform_bypass_flag

                if (reader.ReadBits(1) == 1)
                {
                    var count = chromaFormat != 3 ? 8 : 12;
                    for (var i = 0; i < count; i++)
                    {
                        if (reader.ReadBits(1) == 1)
                        {
                            SkipScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.ReadUnsignedExpGolomb(); // log2_max_frame_num_minus4

            var picOrderCntType = (int)reader.ReadUnsignedExpGolomb();
            if (picOrderCntType == 0)
            {
                reader.ReadUnsignedExpGolomb();
            }
            else if (picOrderCntType == 1)
            {
                reader.ReadBits(1);
                reader.ReadSignedExpGolomb();
                reader.ReadSignedExpGolomb();
                var cycle = (int)reader.ReadUnsignedExpGolomb();
                for (var i = 0; i < cycle; i++)
                {
                    reader.ReadSignedExpGolomb();
                }
            }

            reader.ReadUnsignedExpGolomb(); // max_num_ref_frames
            reader.ReadBits(1); // gaps_in_frame_num_value_allowed_flag

            var widthMbs = (int)reader.ReadUnsignedExpGolomb() + 1;
            var heightMapUnits = (int)reader.ReadUnsignedExpGolomb() + 1;
            var frameMbsOnly = reader.ReadBits(1);

            if (frameMbsOnly == 0)
            {
                reader.ReadBits(1); // mb_adaptive_frame_field_flag
            }

            reader.ReadBits(1); // direct_8x8_inference_flag

            var cropLeft = 0;
            var cropRight = 0;
            var cropTop = 0;
            var cropBottom = 0;

            if (reader.ReadBits(1) == 1)
            {
                cropLeft = (int)reader.ReadUnsignedExpGolomb();
                cropRight = (int)reader.ReadUnsignedExpGolomb();
                cropTop = (int)reader.ReadUnsignedExpGolomb();
                cropBottom = (int)reader.ReadUnsignedExpGolomb();
            }

            var subWidthC = chromaFormat is 1 or 2 ? 2 : 1;
            var subHeightC = chromaFormat == 1 ? 2 : 1;

            var width = widthMbs * 16 - (cropLeft + cropRight) * subWidthC;
            var height = (2 - frameMbsOnly) * heightMapUnits * 16 - (cropTop + cropBottom) * subHeightC;

            return width is > 0 and < 8192 && height is > 0 and < 8192 ? (width, height) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static byte[] RemoveEmulationPrevention(byte[] data)
    {
        var result = new byte[data.Length];
        var count = 0;
        var zeros = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            result[count++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return result.AsSpan(0, count).ToArray();
    }

    private static void SkipScalingList(BitReader reader, int size)
    {
        var last = 8;
        var next = 8;
        for (var i = 0; i < size; i++)
        {
            if (next != 0)
            {
                next = (last + reader.ReadSignedExpGolomb() + 256) % 256;
            }

            if (next != 0)
            {
                last = next;
            }
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;

        public BitReader(byte[] data) => _data = data;

        public int ReadBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var index = _bit >> 3;
                if (index >= _data.Length)
                {
                    throw new EndOfStreamException();
                }

                var bit = (_data[index] >> (7 - (_bit & 7))) & 1;
                value = (value << 1) | bit;
                _bit++;
            }

            return value;
        }

        public uint ReadUnsignedExpGolomb()
        {
            var leadingZeros = 0;
            while (ReadBits(1) == 0)
            {
                leadingZeros++;
                if (leadingZeros > 31)
                {
                    throw new InvalidDataException("Invalid exp-Golomb code.");
                }
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            var value = (uint)ReadBits(leadingZeros);
            return (1u << leadingZeros) - 1 + value;
        }

        public int ReadSignedExpGolomb()
        {
            var value = (int)ReadUnsignedExpGolomb();
            return (value & 1) == 1 ? (value + 1) / 2 : -(value / 2);
        }
    }
}
