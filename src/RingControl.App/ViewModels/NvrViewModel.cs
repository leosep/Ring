using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingControl.App.Services;
using RingControl.App.Views;
using RingControl.Core.Nvr;

namespace RingControl.App.ViewModels;

/// <summary>Recording mode shown in the selector.</summary>
public sealed record NvrModeOption(NvrMode Mode, string Label);

/// <summary>A recording in the local NVR library.</summary>
public sealed partial class NvrClipViewModel : ObservableObject
{
    private readonly NvrViewModel _owner;

    public NvrClipViewModel(NvrClip clip, NvrViewModel owner)
    {
        Clip = clip;
        _owner = owner;
    }

    public NvrClip Clip { get; }

    public string DeviceName => Clip.DeviceName;

    public string TriggerLabel => Clip.TriggerLabel;

    public string TimeLabel => Clip.StartedAt.ToLocalTime()
        .ToString("ddd d MMM · HH:mm:ss", new CultureInfo("en-US"));

    public string DetailLabel
    {
        get
        {
            var parts = new List<string>();

            if (Clip.DurationSeconds > 0)
            {
                parts.Add(Clip.DurationLabel);
            }

            parts.Add(Clip.SizeLabel);

            if (Clip.Width > 0 && Clip.Height > 0)
            {
                parts.Add($"{Clip.Width}×{Clip.Height}");
            }

            return string.Join(" · ", parts);
        }
    }

    public string Glyph => Clip.Trigger switch
    {
        RecordingTrigger.Ding => "\uE7ED",
        RecordingTrigger.Motion => "\uE7F4",
        RecordingTrigger.OnDemand => "\uE714",
        RecordingTrigger.Manual => "\uE768",
        _ => "\uE7C5",
    };

    public bool IsIncomplete => !Clip.Complete;

    [RelayCommand]
    private void Play() => _owner.PlayClip(this);

    [RelayCommand]
    private void Delete() => _owner.DeleteClip(this);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenClipFolder(this);
}

/// <summary>
/// Drives the in-app NVR: start/stop, recording mode, disk usage, retention and the
/// list of local recordings with playback.
/// </summary>
public sealed partial class NvrViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RingSession _session;
    private readonly VlcService _vlc;
    private readonly NvrService _service;
    private readonly Action<string> _report;
    private readonly DispatcherTimer _timer;

    private bool _refreshing;
    private bool _disposed;

    public NvrViewModel(RingSession session, AppSettings settings, VlcService vlc, Action<string> report)
    {
        _session = session;
        _vlc = vlc;
        _report = report;
        _service = new NvrService(session, settings, report);

        Modes = new[]
        {
            new NvrModeOption(NvrMode.Events, "Events"),
            new NvrModeOption(NvrMode.Continuous, "Continuous"),
            new NvrModeOption(NvrMode.Both, "Both"),
        };

        selectedMode = Modes.FirstOrDefault(m => m.Mode == _service.Options.Mode) ?? Modes[0];
        FolderLabel = _service.Options.RootFolder;
        RetentionLabel = BuildRetentionLabel();

        _service.Message += OnServiceMessage;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _timer.Start();

        _ = RefreshAsync();
    }

    public IReadOnlyList<NvrModeOption> Modes { get; }

    public ObservableCollection<NvrClipViewModel> Clips { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool isRunning;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string usageLabel = "Calculating…";

    [ObservableProperty]
    private string clipsLabel = "No recordings";

    [ObservableProperty]
    private string freeLabel = "—";

    [ObservableProperty]
    private string folderLabel = string.Empty;

    [ObservableProperty]
    private string retentionLabel = string.Empty;

    [ObservableProperty]
    private string deviceStatusLabel = "No active cameras";

    [ObservableProperty]
    private string? lastMessage;

    [ObservableProperty]
    private bool hasClips;

    [ObservableProperty]
    private bool hasExternalDaemon;

    [ObservableProperty]
    private string? externalDaemonText;

    [ObservableProperty]
    private NvrModeOption selectedMode;

    public string ConfigPath => _service.ConfigPath;

    public bool IsNotRunning => !IsRunning;

    public DeviceRecorder? GetRecorder(long deviceId) => _service.GetRecorder(deviceId);

    public string StatusLabel => IsRunning ? "Recording" : "Stopped";

    public string ToggleButtonText => IsRunning ? "Stop NVR" : "Start NVR";

    partial void OnSelectedModeChanged(NvrModeOption value)
    {
        if (value is null || _disposed)
        {
            return;
        }

        _ = ApplyModeAsync(value);
    }

    private async Task ApplyModeAsync(NvrModeOption option)
    {
        try
        {
            if (_service.Options.Mode == option.Mode)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;

            await _service.ApplyModeAsync(option.Mode, await GetDevicesAsync().ConfigureAwait(true))
                .ConfigureAwait(true);

            RetentionLabel = BuildRetentionLabel();
            _report($"NVR: {option.Label.ToLowerInvariant()} mode.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Change the NVR mode", ex);
            ErrorMessage = "Could not change the mode: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            if (_service.IsRunning)
            {
                await _service.StopAsync().ConfigureAwait(true);
                IsRunning = false;
                _report("NVR stopped.");
            }
            else
            {
                var devices = await GetDevicesAsync().ConfigureAwait(true);
                await _service.StartAsync(devices).ConfigureAwait(true);
                IsRunning = true;
                _report($"NVR started in {NvrService.ModeLabel(_service.Options.Mode).ToLowerInvariant()} mode.");
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Start or stop the NVR", ex);
            ErrorMessage = ex.Message;
            IsRunning = _service.IsRunning;
        }
        finally
        {
            IsBusy = false;
            UpdateExternalDaemon();
        }
    }

    public async Task StartIfEnabledAsync(bool enabled)
    {
        if (!enabled || _service.IsRunning)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var devices = await GetDevicesAsync().ConfigureAwait(true);
            await _service.StartAsync(devices).ConfigureAwait(true);
            IsRunning = true;
            _report($"NVR started in {NvrService.ModeLabel(_service.Options.Mode).ToLowerInvariant()} mode.");
        }
        catch (Exception ex)
        {
            AppLog.Error("NVR autostart", ex);
            ErrorMessage = "Could not start the NVR automatically: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            UpdateExternalDaemon();
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Keeps the NVR folder in sync with the recording folder chosen in the settings.
    /// The running engine is restarted so new clips go to the new place.
    /// </summary>
    public async Task SyncRecordingFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) ||
            string.Equals(_service.Options.RootFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var wasRunning = _service.IsRunning;

        try
        {
            if (wasRunning)
            {
                await _service.StopAsync().ConfigureAwait(true);
            }

            _service.Options.RootFolder = folder;
            _service.SaveOptions();
            FolderLabel = folder;

            if (wasRunning)
            {
                var devices = await GetDevicesAsync().ConfigureAwait(true);
                await _service.StartAsync(devices).ConfigureAwait(true);
            }

            IsRunning = _service.IsRunning;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Change the NVR folder", ex);
            ErrorMessage = "Could not change the NVR folder: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        _refreshing = true;

        try
        {
            var usage = await Task.Run(() => _service.GetUsage()).ConfigureAwait(true);
            var clips = await Task.Run(() => _service.ListClips()).ConfigureAwait(true);

            UsageLabel = $"{NvrStorage.FormatBytes(usage.Bytes)} · {usage.ClipCount} clip(s)";
            FreeLabel = usage.TotalBytes > 0 ? NvrStorage.FormatBytes(usage.FreeBytes) : "—";
            ClipsLabel = usage.ClipCount == 0
                ? "No recordings"
                : $"{usage.ClipCount} recording(s) · {NvrStorage.FormatBytes(usage.Bytes)}";

            var recent = clips.Take(200).Select(clip => new NvrClipViewModel(clip, this)).ToList();
            Clips.Clear();
            foreach (var clip in recent)
            {
                Clips.Add(clip);
            }

            HasClips = Clips.Count > 0;

            var statuses = _service.GetDeviceStatuses();
            DeviceStatusLabel = statuses.Count == 0
                ? (IsRunning ? "Connecting to the cameras…" : "No active cameras")
                : string.Join("  ·  ", statuses.Select(s => $"{s.DeviceName}: {s.StateLabel}"));

            RetentionLabel = BuildRetentionLabel();
            IsRunning = _service.IsRunning;
            UpdateExternalDaemon();
        }
        catch (Exception ex)
        {
            AppLog.Error("Refresh the NVR state", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task EnforceRetentionAsync()
    {
        try
        {
            IsBusy = true;
            var removed = await Task.Run(() => _service.EnforceRetention()).ConfigureAwait(true);
            _report(removed == 0
                ? "NVR: there are no recordings to delete according to retention."
                : $"NVR: deleted {removed} old recording(s).");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not apply retention: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder() => OpenInExplorer(_service.Options.RootFolder);

    public void OpenClipFolder(NvrClipViewModel item)
        => OpenInExplorer(Path.GetDirectoryName(item.Clip.FilePath) ?? _service.Options.RootFolder);

    public void PlayClip(NvrClipViewModel item)
    {
        try
        {
            var window = new ClipWindow(_vlc, item.Clip.FilePath,
                $"{item.DeviceName} · {item.TriggerLabel} · {item.TimeLabel}")
            {
                Owner = Application.Current?.MainWindow,
            };

            window.Show();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not open the recording: " + ex.Message;
        }
    }

    public void DeleteClip(NvrClipViewModel item)
    {
        var confirm = MessageBox.Show(
            $"Delete the {item.DeviceName} recording from {item.TimeLabel}?\n\nThis action cannot be undone.",
            "Delete recording",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_service.DeleteClip(item.Clip))
        {
            ErrorMessage = "Could not delete the recording.";
            return;
        }

        Clips.Remove(item);
        HasClips = Clips.Count > 0;
        _report("Recording deleted.");
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private async Task<IReadOnlyList<RingControl.Core.Models.RingDevice>> GetDevicesAsync()
        => await _session.RequireApi().GetDevicesAsync().ConfigureAwait(true);

    private void OnServiceMessage(object? sender, string message)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() =>
            {
                LastMessage = message;
                UpdateExternalDaemon();
            });
            return;
        }

        LastMessage = message;
    }

    private void UpdateExternalDaemon()
    {
        var pid = NvrService.FindExternalDaemonPid();
        HasExternalDaemon = pid > 0 && !_service.IsRunning;
        ExternalDaemonText = HasExternalDaemon
            ? $"A background NVR (CLI) is running, process {pid}. The NVR in this window is stopped to avoid duplicate recording."
            : null;
    }

    private string BuildRetentionLabel()
    {
        var options = _service.Options;
        var retention = options.RetentionDays == 0 ? "no day limit" : $"{options.RetentionDays} days";
        var quota = options.MaxStorageBytes == 0
            ? "no quota"
            : NvrStorage.FormatBytes(options.MaxStorageBytes);

        return $"{retention} · {quota}";
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open the NVR folder", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _service.Message -= OnServiceMessage;

        // Keep the continuation on the UI thread: device view models raise property
        // changes and stop timers while being disposed.
        await _service.DisposeAsync();
    }
}
