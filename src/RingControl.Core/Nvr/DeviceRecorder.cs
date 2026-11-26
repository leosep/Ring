using RingControl.Core.Api;
using RingControl.Core.Live;
using RingControl.Core.Media;
using RingControl.Core.Models;

namespace RingControl.Core.Nvr;

public enum DeviceRecorderState
{
    Stopped,
    Connecting,
    Recording,
    Backoff,
    Error,
}

/// <summary>Result of asking a recorder to lend its camera stream to the desktop preview.</summary>
public enum PreviewAttachResult
{
    /// <summary>The recorder is not running; the caller can open its own Ring session.</summary>
    NotAvailable,

    /// <summary>The recorder stream is shared: RTP is forwarded to the local SDP endpoint.</summary>
    Shared,

    /// <summary>The recorder keeps running but will not open new streams while the preview is active.</summary>
    Reserved,

    /// <summary>The recorder is recording a short event clip; the caller should try again shortly.</summary>
    Busy,
}

/// <summary>Local SDP endpoint that receives the RTP packets of a recorder stream.</summary>
public sealed record LivePreviewSource(string SdpFilePath, int Port, int PayloadType);

public sealed record DeviceRecorderStatus(
    long DeviceId,
    string DeviceName,
    DeviceRecorderState State,
    string Message,
    string? CurrentFile,
    double CurrentSeconds,
    long CurrentBytes,
    int ClipsRecorded,
    DateTimeOffset? LastClipAt)
{
    public string StateLabel => State switch
    {
        DeviceRecorderState.Connecting => "Connecting",
        DeviceRecorderState.Recording => "Recording",
        DeviceRecorderState.Backoff => "Retrying",
        DeviceRecorderState.Error => "Error",
        _ => "Stopped",
    };
}

/// <summary>
/// Records one Ring camera. It owns a Ring live-view session, writes H.264 RTP straight
/// into fragmented MP4 segments and reconnects automatically. Used by both the Linux
/// daemon and the Windows GUI.
/// </summary>
public sealed class DeviceRecorder : IAsyncDisposable
{
    private readonly RingApiClient _api;
    private readonly NvrStorage _storage;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _recordGate = new(1, 1);
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private RingLiveStream? _stream;
    private H264Mp4Writer? _writer;
    private NvrClip? _clip;
    private volatile bool _streamFailed;
    private volatile bool _previewWanted;
    private DateTimeOffset _lastPacketAt;
    private int _clipsRecorded;
    private DateTimeOffset? _lastClipAt;
    private DeviceRecorderState _state = DeviceRecorderState.Stopped;
    private string _message = "Stopped";

    public DeviceRecorder(RingApiClient api, RingDevice device, NvrOptions options, NvrStorage storage, Action<string>? log = null)
    {
        _api = api;
        Device = device;
        Options = options;
        _storage = storage;
        _log = log;
    }

    public RingDevice Device { get; }

    public NvrOptions Options { get; }

    public event EventHandler<DeviceRecorderStatus>? StatusChanged;

    /// <summary>
    /// Raised when a shared preview endpoint becomes available, including after the recorder
    /// reconnects (the SDP file and UDP port change on every new Ring session).
    /// </summary>
    public event EventHandler<LivePreviewSource>? PreviewSourceChanged;

    public DeviceRecorderState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>The current local endpoint of the recorder stream, or null when it is not streaming.</summary>
    public LivePreviewSource? PreviewSource
    {
        get
        {
            RingLiveStream? stream;
            lock (_sync)
            {
                stream = _stream;
            }

            return stream is not null && !string.IsNullOrWhiteSpace(stream.SdpFilePath)
                ? new LivePreviewSource(stream.SdpFilePath, stream.LocalVideoPort, stream.VideoPayloadType)
                : null;
        }
    }

    /// <summary>
    /// Asks the recorder to lend its camera stream to the desktop player. Ring only allows one
    /// live session per camera, so sharing the recording stream is the only way to watch the
    /// camera while the NVR records it. The caller must call <see cref="EndPreview"/> when done.
    /// </summary>
    public PreviewAttachResult AttachPreview(out LivePreviewSource? source)
    {
        source = null;

        if (!IsRunning)
        {
            return PreviewAttachResult.NotAvailable;
        }

        if (Options.Mode == NvrMode.Events)
        {
            // Event clips are short-lived. Sharing their stream would end the preview when the
            // clip closes, so instead the recorder pauses event recording while the user watches.
            if (_stream is not null)
            {
                return PreviewAttachResult.Busy;
            }

            _previewWanted = true;
            return PreviewAttachResult.Reserved;
        }

        _previewWanted = true;

        RingLiveStream? stream;
        lock (_sync)
        {
            stream = _stream;
        }

        if (stream is not null && !string.IsNullOrWhiteSpace(stream.SdpFilePath))
        {
            stream.ForwardEnabled = true;
            source = new LivePreviewSource(stream.SdpFilePath, stream.LocalVideoPort, stream.VideoPayloadType);
        }

        return PreviewAttachResult.Shared;
    }

    /// <summary>Stops lending the stream. Recording is not affected.</summary>
    public void EndPreview()
    {
        _previewWanted = false;

        RingLiveStream? stream;
        lock (_sync)
        {
            stream = _stream;
        }

        if (stream is not null)
        {
            stream.ForwardEnabled = false;
        }
    }

    public Task StartAsync()
    {
        if (_loopTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loopTask = Task.Run(() => RunAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var task = _loopTask;
        _cts = null;
        _loopTask = null;

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // Ignore.
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The loop reports its own errors.
            }
        }

        cts?.Dispose();
        await CloseClipAsync().ConfigureAwait(false);
        await CloseStreamAsync().ConfigureAwait(false);
        SetState(DeviceRecorderState.Stopped, "Stopped");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (Options.Mode == NvrMode.Events)
            {
                await RunEventsAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await RunContinuousAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log("Recording loop stopped: " + ex);
            SetState(DeviceRecorderState.Error, "Error: " + ex.Message);
        }
        finally
        {
            await CloseClipAsync().ConfigureAwait(false);
            await CloseStreamAsync().ConfigureAwait(false);
        }
    }

    private async Task RunContinuousAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);
        var segment = TimeSpan.FromMinutes(Options.SegmentMinutes);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await OpenStreamAsync(ct).ConfigureAwait(false);
                await StartClipAsync(RecordingTrigger.Continuous, null).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(5);

                var lastSize = _writer?.BytesWritten ?? 0;
                _lastPacketAt = DateTimeOffset.UtcNow;

                while (!ct.IsCancellationRequested && !_streamFailed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                    var writer = _writer;
                    if (writer is null)
                    {
                        break;
                    }

                    var size = writer.BytesWritten;
                    if (size != lastSize)
                    {
                        lastSize = size;
                        _lastPacketAt = DateTimeOffset.UtcNow;
                    }

                    RaiseStatus();

                    if (writer.Duration >= segment)
                    {
                        await CloseClipAsync().ConfigureAwait(false);
                        await StartClipAsync(RecordingTrigger.Continuous, null).ConfigureAwait(false);
                        lastSize = 0;
                        _lastPacketAt = DateTimeOffset.UtcNow;
                    }
                    else if (DateTimeOffset.UtcNow - _lastPacketAt > TimeSpan.FromSeconds(20))
                    {
                        Log("The camera stopped sending video; reconnecting.");
                        _streamFailed = true;
                    }
                }

                await CloseClipAsync().ConfigureAwait(false);
                await CloseStreamAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("Continuous recording error: " + ex.Message);
                SetState(DeviceRecorderState.Backoff, "Retrying in " + (int)backoff.TotalSeconds + " s: " + ex.Message);
                await CloseClipAsync().ConfigureAwait(false);
                await CloseStreamAsync().ConfigureAwait(false);

                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 120));
            }
        }
    }

    private async Task RunEventsAsync(CancellationToken ct)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        var backoff = TimeSpan.FromSeconds(10);

        await SeedKnownEventsAsync(known, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Options.EventPollSeconds), ct).ConfigureAwait(false);

                var events = await _api.GetEventsAsync(Device, 1, ct).ConfigureAwait(false);
                var candidates = new List<RingEvent>();

                foreach (var ringEvent in events.OrderBy(e => e.CreatedAt))
                {
                    if (string.IsNullOrWhiteSpace(ringEvent.Id) || known.Contains(ringEvent.Id))
                    {
                        continue;
                    }

                    known.Add(ringEvent.Id);

                    // Do not chase events that Ring reports late: only fresh ones start a clip.
                    if (DateTimeOffset.UtcNow - ringEvent.CreatedAt > TimeSpan.FromMinutes(2))
                    {
                        continue;
                    }

                    if (IsTrigger(ringEvent))
                    {
                        candidates.Add(ringEvent);
                    }
                }

                if (known.Count > 500)
                {
                    known.Clear();
                    await SeedKnownEventsAsync(known, ct).ConfigureAwait(false);
                }

                var trigger = candidates.FirstOrDefault();
                if (trigger is not null)
                {
                    var kind = TriggerFor(trigger);
                    Log($"Event {trigger.KindLabel} ({trigger.Id}); recording {Options.EventClipSeconds} s.");
                    await RecordClipAsync(TimeSpan.FromSeconds(Options.EventClipSeconds), kind, trigger.Id, ct).ConfigureAwait(false);
                }

                backoff = TimeSpan.FromSeconds(10);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("Error polling events: " + ex.Message);
                SetState(DeviceRecorderState.Backoff, "Retrying in " + (int)backoff.TotalSeconds + " s");
                await CloseClipAsync().ConfigureAwait(false);
                await CloseStreamAsync().ConfigureAwait(false);

                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
            }
        }
    }

    private async Task SeedKnownEventsAsync(HashSet<string> known, CancellationToken ct)
    {
        try
        {
            var events = await _api.GetEventsAsync(Device, 1, ct).ConfigureAwait(false);
            foreach (var ringEvent in events)
            {
                if (!string.IsNullOrWhiteSpace(ringEvent.Id))
                {
                    known.Add(ringEvent.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Could not read the initial events: " + ex.Message);
        }
    }

    private bool IsTrigger(RingEvent ringEvent)
    {
        return ringEvent.Kind.ToLowerInvariant() switch
        {
            "ding" => Options.RecordOnDing,
            "motion" => Options.RecordOnMotion,
            "on_demand" or "on_demand_link" => Options.RecordOnDemand,
            _ => false,
        };
    }

    private static RecordingTrigger TriggerFor(RingEvent ringEvent) => ringEvent.Kind.ToLowerInvariant() switch
    {
        "ding" => RecordingTrigger.Ding,
        "motion" => RecordingTrigger.Motion,
        "on_demand" or "on_demand_link" => RecordingTrigger.OnDemand,
        _ => RecordingTrigger.Manual,
    };

    /// <summary>Records a one-off clip. Returns false when a recording is already in progress.</summary>
    public async Task<bool> RecordNowAsync(TimeSpan duration, RecordingTrigger trigger = RecordingTrigger.Manual,
        string? eventId = null, CancellationToken ct = default)
    {
        if (Options.Mode != NvrMode.Events)
        {
            // Continuous mode is already recording; a manual clip would fight the segmenter.
            return false;
        }

        if (_writer is not null)
        {
            return false;
        }

        await RecordClipAsync(duration, trigger, eventId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task RecordClipAsync(TimeSpan duration, RecordingTrigger trigger, string? eventId, CancellationToken ct)
    {
        await _recordGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                return;
            }

            if (_previewWanted)
            {
                // The desktop is watching the camera with its own Ring session; opening a
                // second one would fail. Skip this event instead of fighting for the device.
                Log("Skipping event recording: a live view is active.");
                return;
            }

            await OpenStreamAsync(ct).ConfigureAwait(false);
            await StartClipAsync(trigger, eventId).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + duration;
            _lastPacketAt = DateTimeOffset.UtcNow;
            var lastSize = 0L;

            while (!ct.IsCancellationRequested && !_streamFailed && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                var writer = _writer;
                if (writer is null)
                {
                    break;
                }

                if (writer.BytesWritten != lastSize)
                {
                    lastSize = writer.BytesWritten;
                    _lastPacketAt = DateTimeOffset.UtcNow;
                }

                RaiseStatus();

                if (DateTimeOffset.UtcNow - _lastPacketAt > TimeSpan.FromSeconds(20))
                {
                    Log("The camera stopped sending video during the event.");
                    break;
                }
            }

            await CloseClipAsync().ConfigureAwait(false);
        }
        finally
        {
            await CloseStreamAsync().ConfigureAwait(false);
            _recordGate.Release();
        }
    }

    private async Task OpenStreamAsync(CancellationToken ct)
    {
        if (_stream is not null)
        {
            return;
        }

        _streamFailed = false;
        SetState(DeviceRecorderState.Connecting, "Connecting to the camera…");

        var stream = new RingLiveStream(_api, Device, Log);
        stream.StatusChanged += OnStreamStatusChanged;
        stream.VideoPayload += OnVideoPayload;

        lock (_sync)
        {
            _stream = stream;
        }

        await stream.StartAsync(ct).ConfigureAwait(false);
        Log("Video session started.");

        if (_previewWanted)
        {
            stream.ForwardEnabled = true;
            RaisePreviewSource(stream);
        }

        // Give the camera a moment to deliver the first packet before declaring success.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (stream.PacketCount == 0 && DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        if (stream.PacketCount == 0)
        {
            throw new TimeoutException("The camera did not send video in time.");
        }
    }

    private async Task CloseStreamAsync()
    {
        RingLiveStream? stream;
        lock (_sync)
        {
            stream = _stream;
            _stream = null;
        }

        if (stream is null)
        {
            return;
        }

        stream.StatusChanged -= OnStreamStatusChanged;
        stream.VideoPayload -= OnVideoPayload;

        try
        {
            await stream.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors.
        }

        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore.
        }
    }

    private void OnStreamStatusChanged(object? sender, LiveStreamStatusEventArgs e)
    {
        if (e.State == LiveStreamState.Failed)
        {
            _streamFailed = true;
            Log("Video session error: " + e.Message);
        }
        else if (e.State is LiveStreamState.Streaming or LiveStreamState.WaitingForVideo)
        {
            _lastPacketAt = DateTimeOffset.UtcNow;
        }
    }

    private void OnVideoPayload(byte[] payload, uint timestamp)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.WritePayload(payload, timestamp);
        }
        catch (Exception ex)
        {
            Log("Error writing video: " + ex.Message);
        }
    }

    private async Task StartClipAsync(RecordingTrigger trigger, string? eventId)
    {
        var started = DateTimeOffset.Now;
        var path = _storage.CreateClipPath(Device.Id, Device.Name, started, trigger);
        var writer = new H264Mp4Writer(path);

        var clip = new NvrClip
        {
            FilePath = path,
            DeviceId = Device.Id,
            DeviceName = Device.Name,
            StartedAt = started,
            EndedAt = started,
            Trigger = trigger,
            EventId = eventId,
            Complete = false,
        };

        clip.Save();

        lock (_sync)
        {
            _writer = writer;
            _clip = clip;
        }

        _lastPacketAt = DateTimeOffset.UtcNow;
        SetState(DeviceRecorderState.Recording, "Recording " + Path.GetFileName(path));
        await Task.CompletedTask;
    }

    private async Task CloseClipAsync()
    {
        H264Mp4Writer? writer;
        NvrClip? clip;

        lock (_sync)
        {
            writer = _writer;
            clip = _clip;
            _writer = null;
            _clip = null;
        }

        if (writer is null || clip is null)
        {
            return;
        }

        try
        {
            writer.Complete();
        }
        catch (Exception ex)
        {
            Log("Could not close the video file: " + ex.Message);
        }

        clip.EndedAt = DateTimeOffset.Now;
        clip.DurationSeconds = writer.Duration.TotalSeconds;
        clip.Bytes = writer.BytesWritten;
        clip.FramesPerSecond = writer.FramesPerSecond;
        clip.Width = writer.Width;
        clip.Height = writer.Height;

        try
        {
            if (File.Exists(clip.FilePath))
            {
                clip.Bytes = new FileInfo(clip.FilePath).Length;
            }
        }
        catch
        {
            // Ignore.
        }

        if (clip.Bytes < 20_000 || clip.DurationSeconds < 1)
        {
            clip.Complete = false;
            _storage.Delete(clip);
            Log("Discarded an empty recording: " + Path.GetFileName(clip.FilePath));
        }
        else
        {
            clip.Complete = true;
            clip.Save();
            _clipsRecorded++;
            _lastClipAt = clip.EndedAt;
            Log($"Recording saved: {Path.GetFileName(clip.FilePath)} ({clip.DurationLabel}, {clip.SizeLabel}).");
        }

        SetState(DeviceRecorderState.Stopped, "Waiting…");
        await Task.CompletedTask;
    }

    public DeviceRecorderStatus GetStatus()
    {
        H264Mp4Writer? writer;
        NvrClip? clip;

        lock (_sync)
        {
            writer = _writer;
            clip = _clip;
        }

        return new DeviceRecorderStatus(
            Device.Id,
            Device.Name,
            State,
            _message,
            clip?.FilePath,
            writer?.Duration.TotalSeconds ?? 0,
            writer?.BytesWritten ?? 0,
            _clipsRecorded,
            _lastClipAt);
    }

    private void SetState(DeviceRecorderState state, string message)
    {
        lock (_sync)
        {
            _state = state;
            _message = message;
        }

        RaiseStatus();
    }

    private void RaiseStatus()
    {
        try
        {
            StatusChanged?.Invoke(this, GetStatus());
        }
        catch
        {
            // A UI handler must never break recording.
        }
    }

    private void RaisePreviewSource(RingLiveStream stream)
    {
        if (string.IsNullOrWhiteSpace(stream.SdpFilePath))
        {
            return;
        }

        try
        {
            PreviewSourceChanged?.Invoke(this,
                new LivePreviewSource(stream.SdpFilePath, stream.LocalVideoPort, stream.VideoPayloadType));
        }
        catch
        {
            // A player handler must never break recording.
        }
    }

    private void Log(string message) => _log?.Invoke($"[{Device.Name}] {message}");

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _recordGate.Dispose();
    }
}
