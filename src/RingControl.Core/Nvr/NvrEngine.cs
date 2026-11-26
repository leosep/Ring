using RingControl.Core.Api;
using RingControl.Core.Models;

namespace RingControl.Core.Nvr;

/// <summary>
/// Runs the NVR for a set of Ring devices: one <see cref="DeviceRecorder"/> per camera,
/// plus the retention policy. Cross-platform: it only depends on the Core library, so the
/// same engine powers the Linux daemon and the Windows GUI.
/// </summary>
public sealed class NvrEngine : IAsyncDisposable
{
    private readonly RingApiClient _api;
    private readonly NvrOptions _options;
    private readonly Action<string>? _log;
    private readonly List<DeviceRecorder> _recorders = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _retentionTask;
    private bool _started;

    public NvrEngine(RingApiClient api, NvrOptions options, Action<string>? log = null)
    {
        _api = api;
        _options = options;
        _log = log;
        _options.Normalize();
        Storage = new NvrStorage(_options.RootFolder);
    }

    public NvrOptions Options => _options;

    public NvrStorage Storage { get; }

    public IReadOnlyList<DeviceRecorder> Recorders => _recorders;

    public event EventHandler<DeviceRecorderStatus>? StatusChanged;

    public event EventHandler<string>? Message;

    public async Task StartAsync(IReadOnlyList<RingDevice> devices, CancellationToken ct = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Directory.CreateDirectory(_options.RootFolder);

        await SyncDevicesAsync(devices, ct).ConfigureAwait(false);

        _retentionTask = Task.Run(() => RetentionLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Reconciles the running recorders with the current device list: recorders for cameras
    /// that went offline (or were removed from the account) are stopped, new cameras are
    /// started. Called by the daemon periodically so the NVR survives Ring outages.
    /// </summary>
    public async Task SyncDevicesAsync(IReadOnlyList<RingDevice> devices, CancellationToken ct = default)
    {
        if (!_started)
        {
            return;
        }

        var selected = SelectDevices(devices);

        foreach (var recorder in _recorders.ToList())
        {
            if (selected.Any(d => d.Id == recorder.Device.Id))
            {
                continue;
            }

            Log($"NVR disabled for {recorder.Device.Name} (offline or no longer selected).");
            recorder.StatusChanged -= OnRecorderStatus;
            _recorders.Remove(recorder);

            try
            {
                await recorder.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Keep reconciling the rest.
            }

            await recorder.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var device in selected)
        {
            if (_recorders.Any(r => r.Device.Id == device.Id))
            {
                continue;
            }

            if (_recorders.Count >= _options.MaxConcurrentStreams)
            {
                break;
            }

            var recorder = new DeviceRecorder(_api, device, _options, Storage, Log);
            recorder.StatusChanged += OnRecorderStatus;
            _recorders.Add(recorder);
            await recorder.StartAsync().ConfigureAwait(false);
            Log($"NVR enabled for {device.Name} ({_options.Mode}).");
        }

        if (_recorders.Count == 0)
        {
            Log("No cameras available for the NVR (offline or filtered by settings).");
        }
    }

    private List<RingDevice> SelectDevices(IReadOnlyList<RingDevice> devices) => devices
        .Where(d => d.IsOnline && !d.LiveViewDisabled && _options.Includes(d.Id))
        .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
        .Take(_options.MaxConcurrentStreams)
        .ToList();

    public DeviceRecorder? GetRecorder(long deviceId)
        => _recorders.FirstOrDefault(r => r.Device.Id == deviceId);

    public bool IsRecording(long deviceId)
        => GetRecorder(deviceId)?.State == DeviceRecorderState.Recording;

    public async Task<bool> RecordNowAsync(long deviceId, TimeSpan duration,
        RecordingTrigger trigger = RecordingTrigger.Manual, CancellationToken ct = default)
    {
        var recorder = GetRecorder(deviceId);
        return recorder is not null && await recorder.RecordNowAsync(duration, trigger, null, ct).ConfigureAwait(false);
    }

    public NvrStorageUsage GetUsage() => Storage.GetUsage();

    public IReadOnlyList<NvrClip> ListClips(long? deviceId = null) => Storage.ListClips(deviceId);

    public int EnforceRetention()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recorder in _recorders)
        {
            var file = recorder.GetStatus().CurrentFile;
            if (!string.IsNullOrWhiteSpace(file))
            {
                active.Add(file);
            }
        }

        return Storage.EnforceRetention(_options, active);
    }

    private async Task RetentionLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var removed = EnforceRetention();
                    if (removed > 0)
                    {
                        Log($"Retention: deleted {removed} old recording(s).");
                    }
                }
                catch (Exception ex)
                {
                    Log("Error applying retention: " + ex.Message);
                }

                await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void OnRecorderStatus(object? sender, DeviceRecorderStatus status)
        => StatusChanged?.Invoke(this, status);

    private void Log(string message)
    {
        _log?.Invoke(message);
        Message?.Invoke(this, message);
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore.
        }

        if (_retentionTask is not null)
        {
            try
            {
                await _retentionTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore.
            }

            _retentionTask = null;
        }

        foreach (var recorder in _recorders)
        {
            recorder.StatusChanged -= OnRecorderStatus;
        }

        foreach (var recorder in _recorders)
        {
            try
            {
                await recorder.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Keep stopping the rest.
            }

            try
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore disposal errors.
            }
        }

        _recorders.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
