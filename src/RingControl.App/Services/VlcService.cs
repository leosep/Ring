using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace RingControl.App.Services;

/// <summary>Owns the single LibVLC instance and the raw H.264 → MP4 remux helper.</summary>
public sealed class VlcService : IDisposable
{
    private bool _disposed;

    public VlcService()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            LibVlc = new LibVLC(
                "--no-video-title-show",
                "--no-snapshot-preview",
                "--verbose=1",
                "--network-caching=300",
                "--file-caching=300");

            LibVlc.Log += (_, e) =>
            {
                if (e.Level is LogLevel.Error)
                {
                    AppLog.Write($"[vlc] {e.Module}: {e.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            AppLog.Error("LibVLC could not be initialised", ex);
        }
    }

    public LibVLC? LibVlc { get; }

    public string? InitError { get; }

    /// <summary>
    /// Remuxes the Annex-B recording to MP4 with VLC's H.264 demuxer and MP4 muxer (no re-encode).
    /// Falls back to the caller keeping the .h264 file when this throws.
    /// </summary>
    public async Task<string> RemuxToMp4Async(string h264Path, double framesPerSecond, CancellationToken ct)
    {
        if (LibVlc is null)
        {
            throw new InvalidOperationException("LibVLC is not available.");
        }

        if (!File.Exists(h264Path))
        {
            throw new FileNotFoundException("Recording not found.", h264Path);
        }

        var mp4Path = Path.ChangeExtension(h264Path, ".mp4");
        try
        {
            if (File.Exists(mp4Path))
            {
                File.Delete(mp4Path);
            }
        }
        catch
        {
            // Overwrite will be attempted by VLC.
        }

        var fps = Math.Clamp(framesPerSecond, 5, 60).ToString("0.###", CultureInfo.InvariantCulture);
        var destination = mp4Path.Replace('\\', '/').Replace("'", "\\'");

        using var media = new Media(LibVlc, h264Path, FromType.FromPath);
        media.AddOption(":demux=h264");
        media.AddOption($":h264-fps={fps}");
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst='{destination}'}}");
        media.AddOption(":sout-keep");

        using var player = new MediaPlayer(media);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        player.EndReached += (_, _) => completion.TrySetResult(true);
        player.Stopped += (_, _) => completion.TrySetResult(true);
        player.EncounteredError += (_, _) =>
            completion.TrySetException(new InvalidOperationException("VLC could not convert the recording."));

        if (!player.Play())
        {
            throw new InvalidOperationException("VLC could not open the recording.");
        }

        await using var registration = ct.Register(() =>
        {
            try
            {
                player.Stop();
            }
            catch
            {
                // Ignore.
            }

            completion.TrySetCanceled(ct);
        });

        await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

        var info = new FileInfo(mp4Path);
        if (!info.Exists || info.Length == 0)
        {
            throw new InvalidOperationException("VLC did not create the MP4 file.");
        }

        return mp4Path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LibVlc?.Dispose();
    }
}
