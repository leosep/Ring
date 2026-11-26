using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingControl.App.Services;

namespace RingControl.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RingSession _session;
    private readonly AppSettings _settings;
    private readonly VlcService _vlc;
    private readonly Action _persist;
    private DeviceViewModel? _activeDevice;
    private bool _initialized;
    private bool _loadingDevices;
    private bool _disposed;

    public MainViewModel(RingSession session, AppSettings settings, VlcService vlc, Action persist)
    {
        _session = session;
        _settings = settings;
        _vlc = vlc;
        _persist = persist;
        Nvr = new NvrViewModel(session, settings, vlc, ReportStatus);
    }

    public NvrViewModel Nvr { get; }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    [ObservableProperty]
    private DeviceViewModel? selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool isLoading = true;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string statusMessage = "Conectado a Ring.";

    [ObservableProperty]
    private bool isRefreshing;

    public bool ShowEmptyState => !IsLoading && Devices.Count == 0;

    public string AccountLabel => string.IsNullOrWhiteSpace(_settings.RememberedEmail) ? "Cuenta de Ring" : _settings.RememberedEmail!;

    public string AccountInitial => string.IsNullOrWhiteSpace(AccountLabel) ? "R" : AccountLabel[..1].ToUpperInvariant();

    public event Action? LogoutRequested;

    public event Action? SettingsRequested;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await LoadDevicesAsync();
        await Nvr.StartIfEnabledAsync(_settings.NvrAutoStart);
    }

    /// <summary>Applies a recording-folder change from the settings dialog to the NVR.</summary>
    public async Task SyncRecordingFolderAsync(string previousFolder)
    {
        if (!string.Equals(previousFolder, _settings.RecordingFolder, StringComparison.OrdinalIgnoreCase))
        {
            await Nvr.SyncRecordingFolderAsync(_settings.RecordingFolder);
        }
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        if (_loadingDevices)
        {
            return;
        }

        _loadingDevices = true;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var preferredId = SelectedDevice?.Device.Id ?? _settings.LastDeviceId;
            var devices = await _session.RequireApi().GetDevicesAsync();

            foreach (var device in Devices)
            {
                await device.DeactivateAsync();
            }

            Devices.Clear();

            foreach (var device in devices)
            {
                Devices.Add(new DeviceViewModel(device, _session, _settings, _vlc, ReportStatus,
                    id => Nvr.GetRecorder(id)));
            }

            SelectedDevice = Devices.FirstOrDefault(d => d.Device.Id == preferredId) ?? Devices.FirstOrDefault();

            StatusMessage = Devices.Count == 0
                ? "No se encontraron cámaras ni timbres en esta cuenta."
                : $"{Devices.Count} dispositivo(s) disponibles.";

            OnPropertyChanged(nameof(ShowEmptyState));
        }
        catch (Exception ex)
        {
            AppLog.Error("Cargar dispositivos", ex);
            ErrorMessage = ex.Message;
            StatusMessage = "No se pudieron cargar los dispositivos.";
            OnPropertyChanged(nameof(ShowEmptyState));
        }
        finally
        {
            IsLoading = false;
            _loadingDevices = false;
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void Logout()
    {
        var result = MessageBox.Show(
            "¿Quieres cerrar la sesión de Ring en este equipo?\n\nSe eliminará la sesión guardada y tendrás que volver a iniciar sesión.",
            "Cerrar sesión",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            LogoutRequested?.Invoke();
        }
    }

    private void ReportStatus(string message)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => StatusMessage = message);
            return;
        }

        StatusMessage = message;
    }

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        if (_activeDevice is not null && !ReferenceEquals(_activeDevice, value))
        {
            _ = _activeDevice.DeactivateAsync();
        }

        _activeDevice = value;

        if (value is null)
        {
            return;
        }

        _settings.LastDeviceId = value.Device.Id;
        _persist();
        _ = value.ActivateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await Nvr.DisposeAsync();

        foreach (var device in Devices)
        {
            await device.DisposeAsync();
        }

        Devices.Clear();
    }
}
