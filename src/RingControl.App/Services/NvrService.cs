using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RingControl.Core.Models;
using RingControl.Core.Nvr;
using RingControl.Core.Platform;

namespace RingControl.App.Services;

/// <summary>
/// Hosts the shared NVR engine inside the desktop app. It uses the same
/// <c>nvr.json</c> as the Linux daemon, so retention, folder layout and metadata are
/// identical. Only one NVR should run per account: if the CLI daemon is already
/// running, the GUI refuses to start a second one.
/// </summary>
public sealed class NvrService : IAsyncDisposable
{
    private readonly RingSession _session;
    private readonly NvrConfigStore _store = new();
    private readonly Action<string> _report;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NvrEngine? _engine;

    public NvrService(RingSession session, AppSettings settings, Action<string> report)
    {
        _session = session;
        _report = report;

        if (_store.Exists)
        {
            Options = _store.Load(createIfMissing: false);
        }
        else
        {
            Options = new NvrOptions { RootFolder = settings.RecordingFolder };
            _store.Save(Options);
        }
    }

    public NvrOptions Options { get; private set; }

    public string ConfigPath => _store.Path;

    public NvrStorage Storage => _engine?.Storage ?? new NvrStorage(Options.RootFolder);

    public bool IsRunning => _engine is not null;

    public event EventHandler<DeviceRecorderStatus>? DeviceStatusChanged;

    public event EventHandler<string>? Message;

    public void SaveOptions()
    {
        try
        {
            _store.Save(Options);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save the NVR settings", ex);
        }
    }

    public async Task StartAsync(IReadOnlyList<RingDevice> devices, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_engine is not null)
            {
                return;
            }

            var externalPid = FindExternalDaemonPid();
            if (externalPid > 0)
            {
                throw new InvalidOperationException(
                    $"The NVR is already running from the command line (process {externalPid}). " +
                    "Stop it with 'ringcontrol nvr stop' before starting it here.");
            }

            Options.Normalize();
            Directory.CreateDirectory(Options.RootFolder);

            var engine = new NvrEngine(_session.RequireApi(), Options, Log);
            engine.StatusChanged += OnEngineStatusChanged;
            engine.Message += OnEngineMessage;

            try
            {
                await engine.StartAsync(devices, ct).ConfigureAwait(false);
            }
            catch
            {
                engine.StatusChanged -= OnEngineStatusChanged;
                engine.Message -= OnEngineMessage;
                await engine.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _engine = engine;
            Log($"NVR active ({ModeLabel(Options.Mode)} mode).");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            var engine = _engine;
            _engine = null;

            if (engine is null)
            {
                return;
            }

            engine.StatusChanged -= OnEngineStatusChanged;
            engine.Message -= OnEngineMessage;

            try
            {
                await engine.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }

            Log("NVR stopped.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Changes the recording mode; if the NVR is running it restarts with the new mode.</summary>
    public async Task ApplyModeAsync(NvrMode mode, IReadOnlyList<RingDevice> devices, CancellationToken ct = default)
    {
        if (Options.Mode == mode)
        {
            return;
        }

        var wasRunning = IsRunning;
        Options.Mode = mode;
        SaveOptions();

        if (!wasRunning)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        await StartAsync(devices, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<DeviceRecorderStatus> GetDeviceStatuses()
        => _engine?.Recorders.Select(r => r.GetStatus()).ToList() ?? new List<DeviceRecorderStatus>();

    /// <summary>Returns the running recorder for a camera, used to share its stream with the preview.</summary>
    public DeviceRecorder? GetRecorder(long deviceId) => _engine?.GetRecorder(deviceId);

    public NvrStorageUsage GetUsage() => Storage.GetUsage();

    public IReadOnlyList<NvrClip> ListClips(long? deviceId = null) => Storage.ListClips(deviceId);

    public int EnforceRetention() => _engine?.EnforceRetention() ?? 0;

    public bool DeleteClip(NvrClip clip) => Storage.Delete(clip);

    public static string ModeLabel(NvrMode mode) => mode switch
    {
        NvrMode.Continuous => "Continuous (24/7)",
        NvrMode.Both => "Continuous + events",
        _ => "Events only",
    };

    /// <summary>
    /// Looks for the CLI daemon through <c>nvr-status.json</c> (written every 5 seconds).
    /// A stale file (daemon crashed) is ignored.
    /// </summary>
    public static int FindExternalDaemonPid()
    {
        try
        {
            var path = Path.Combine(PlatformPaths.ConfigDirectory, "nvr-status.json");
            if (!File.Exists(path))
            {
                return 0;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (!root.TryGetProperty("Running", out var running) || !running.GetBoolean())
            {
                return 0;
            }

            if (!root.TryGetProperty("UpdatedAt", out var updated) ||
                !DateTimeOffset.TryParse(updated.GetString(), out var updatedAt) ||
                DateTimeOffset.Now - updatedAt > TimeSpan.FromMinutes(2))
            {
                return 0;
            }

            if (!root.TryGetProperty("Pid", out var pidElement) || !pidElement.TryGetInt32(out var pid) || pid <= 0)
            {
                return 0;
            }

            using var process = Process.GetProcessById(pid);
            return process.HasExited ? 0 : pid;
        }
        catch
        {
            return 0;
        }
    }

    private void OnEngineStatusChanged(object? sender, DeviceRecorderStatus status)
        => DeviceStatusChanged?.Invoke(this, status);

    private void OnEngineMessage(object? sender, string message) => Message?.Invoke(this, message);

    private void Log(string message)
    {
        AppLog.Write("NVR: " + message);
        _report(message);
        Message?.Invoke(this, message);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
