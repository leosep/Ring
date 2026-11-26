namespace RingControl.Core.Live;

/// <summary>
/// Depacketises H264 RTP (RFC 6184) into an Annex-B elementary stream. Ring sends H264 with
/// packetization-mode=1, so FU-A fragments, STAP-A aggregates and single NAL units all need
/// to be handled. The resulting .h264 file is lossless and can be converted to MP4 later.
/// </summary>
public sealed class H264RtpRecorder : IDisposable
{
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    private readonly object _sync = new();
    private FileStream? _file;
    private MemoryStream? _fragment;
    private byte[]? _sps;
    private byte[]? _pps;
    private bool _parameterSetsWritten;
    private long _bytes;
    private int _nalCount;
    private bool _hasTimestamp;
    private uint _firstTimestamp;
    private uint _lastTimestamp;
    private long _frameCount;

    /// <summary>Frame rate estimated from RTP timestamps, used when remuxing to MP4.</summary>
    public double FramesPerSecond { get; private set; } = 15;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _file is not null;
            }
        }
    }

    public string? FilePath { get; private set; }

    public long BytesWritten
    {
        get
        {
            lock (_sync)
            {
                return _bytes;
            }
        }
    }

    public TimeSpan Duration { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public void Start(string path)
    {
        lock (_sync)
        {
            StopInternal();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: false);
            FilePath = path;
            StartedAt = DateTimeOffset.Now;
            _bytes = 0;
            _nalCount = 0;
            _sps = null;
            _pps = null;
            _parameterSetsWritten = false;
            _fragment = null;
            _hasTimestamp = false;
            _firstTimestamp = 0;
            _lastTimestamp = 0;
            _frameCount = 0;
            FramesPerSecond = 15;
        }
    }

    /// <summary>Called for every H264 RTP payload (the payload type must already be filtered by the caller).</summary>
    public void WritePayload(ReadOnlySpan<byte> payload, uint rtpTimestamp = 0)
    {
        if (payload.Length < 1)
        {
            return;
        }

        lock (_sync)
        {
            if (_file is null)
            {
                return;
            }

            TrackFrame(rtpTimestamp);

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

                        WriteNal(payload.Slice(index, length));
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
                        _fragment = new MemoryStream(payload.Length + 16);
                        _fragment.WriteByte(reconstructedHeader);
                    }

                    if (_fragment is not null)
                    {
                        _fragment.Write(payload[2..]);
                    }

                    if (isEnd && _fragment is not null)
                    {
                        WriteNal(_fragment.ToArray());
                        _fragment.Dispose();
                        _fragment = null;
                    }

                    break;
                }
                case >= 1 and <= 23: // Single NAL unit
                    WriteNal(payload);
                    break;
                default:
                    // 25-27 (MTAP), 29 (FU-B), 30-31 are not used by Ring.
                    break;
            }
        }
    }

    private void TrackFrame(uint timestamp)
    {
        if (timestamp == 0)
        {
            return;
        }

        if (!_hasTimestamp)
        {
            _hasTimestamp = true;
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

    private void WriteNal(ReadOnlySpan<byte> nal)
    {
        if (nal.Length == 0)
        {
            return;
        }

        var type = nal[0] & 0x1F;

        if (type == 7)
        {
            _sps = nal.ToArray();
            _parameterSetsWritten = false;
        }
        else if (type == 8)
        {
            _pps = nal.ToArray();
        }
        else if (type == 5 && !_parameterSetsWritten)
        {
            // Make each keyframe independently decodable.
            if (_sps is not null)
            {
                WriteRaw(_sps);
            }

            if (_pps is not null)
            {
                WriteRaw(_pps);
            }

            _parameterSetsWritten = true;
        }

        WriteRaw(nal);

        _nalCount++;
        if (type == 5 || _nalCount % 120 == 0)
        {
            _file?.Flush();
        }
    }

    private void WriteRaw(ReadOnlySpan<byte> nal)
    {
        if (_file is null)
        {
            return;
        }

        _file.Write(StartCode);
        _file.Write(nal);
        _bytes += StartCode.Length + nal.Length;
        Duration = DateTimeOffset.Now - StartedAt;
    }

    public string? Stop()
    {
        lock (_sync)
        {
            var path = FilePath;
            StopInternal();
            return path;
        }
    }

    private void StopInternal()
    {
        try
        {
            _file?.Flush();
            _file?.Dispose();
        }
        catch
        {
            // Ignore flush errors on shutdown.
        }

        _file = null;
        _fragment?.Dispose();
        _fragment = null;
        FilePath = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopInternal();
        }
    }
}
