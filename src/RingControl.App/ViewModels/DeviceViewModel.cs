using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using RingControl.App.Services;
using RingControl.App.Views;
using RingControl.Core.Live;
using RingControl.Core.Models;
using RingControl.Core.Nvr;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RingControl.App.ViewModels;

public sealed partial class DeviceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RingSession _session;
    private readonly AppSettings _settings;
    private readonly VlcService _vlc;
    private readonly Action<string> _report;
    private readonly Func<long, DeviceRecorder?>? _findRecorder;
    private readonly SemaphoreSlim _thumbnailGate = new(3, 3);
    private readonly DispatcherTimer _uiTimer;

    private CancellationTokenSource? _cts;
    private RingLiveStream? _stream;
    private DeviceRecorder? _previewRecorder;
    private H264RtpRecorder? _recorder;
    private Media? _media;
    private VlcMediaPlayer? _player;
    private bool _sharedPreview;
    private bool _active;
    private bool _disposed;
    private DateTimeOffset _lastForcedSnapshot = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSample;
    private long _lastSampleBytes;

    public DeviceViewModel(RingDevice device, RingSession session, AppSettings settings, VlcService vlc, Action<string> report,
        Func<long, DeviceRecorder?>? findRecorder = null)
    {
        Device = device;
        _session = session;
        _settings = settings;
        _vlc = vlc;
        _report = report;
        _findRecorder = findRecorder;
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateStats();
        MotionOn = device.MotionDetectionEnabled;
        ChimeOn = device.ChimeEnabled;
    }

    public RingDevice Device { get; }

    public string Name => Device.Name;

    public string ModelLabel => Device.ModelLabel;

    public string Subtitle => $"{Device.KindLabel} · {Device.ModelLabel}";

    public bool IsOnline => Device.IsOnline;

    public string StatusLabel => Device.StatusLabel;

    public string BatteryLabel => Device.BatteryLabel;

    public bool HasBattery => Device.BatteryPercent is not null;

    public bool HasLight => Device.HasLight;

    public bool HasSiren => Device.HasSiren;

    public bool HasChime => Device.IsDoorbell;

    public bool LiveViewDisabled => Device.LiveViewDisabled;

    public string? Address => Device.Address;

    public bool HasAddress => !string.IsNullOrWhiteSpace(Device.Address);

    public ObservableCollection<HealthItem> HealthItems { get; } = new();

    public ObservableCollection<EventItemViewModel> Events { get; } = new();

    public string EventCountLabel => Events.Count > 0 ? $"({Events.Count})" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoVisible))]
    private VlcMediaPlayer? mediaPlayer;

    [ObservableProperty]
    private ImageSource? snapshotImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartLive))]
    [NotifyPropertyChangedFor(nameof(LiveButtonText))]
    private bool isLive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartLive))]
    [NotifyPropertyChangedFor(nameof(LiveButtonText))]
    private bool isConnecting;

    [ObservableProperty]
    private string liveStatus = "Sin vídeo en directo";

    [ObservableProperty]
    private string? liveError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    private bool isRecording;

    [ObservableProperty]
    private string? recordingInfo;

    [ObservableProperty]
    private bool isConverting;

    [ObservableProperty]
    private string? snapshotStatus;

    [ObservableProperty]
    private string? actionError;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool lightOn;

    [ObservableProperty]
    private bool sirenOn;

    [ObservableProperty]
    private bool motionOn;

    [ObservableProperty]
    private bool chimeOn;

    [ObservableProperty]
    private bool hasEvents;

    [ObservableProperty]
    private string streamStats = string.Empty;

    [ObservableProperty]
    private string? lastRecordingPath;

    public bool IsVideoVisible => MediaPlayer is not null;

    public bool CanStartLive => !IsLive && !IsConnecting && Device.IsOnline && !Device.LiveViewDisabled;

    public string LiveButtonText => IsLive ? "Detener" : "En directo";

    public string RecordButtonText => IsRecording ? "Detener grabación" : "Grabar";

    public async Task ActivateAsync()
    {
        if (_disposed || _active)
        {
            return;
        }

        _active = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _uiTimer.Start();
        MotionOn = Device.MotionDetectionEnabled;
        ChimeOn = Device.ChimeEnabled;

        await LoadSnapshotAsync(force: false, ct);
        await LoadHealthAsync(ct);
        await LoadEventsAsync(ct);

        _ = SnapshotLoopAsync(ct);

        if (_settings.AutoStartLive && CanStartLive)
        {
            await StartLiveAsync();
        }
    }

    public async Task DeactivateAsync()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _uiTimer.Stop();

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // Ignore.
        }

        await StopLiveAsync();

        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ActionError = null;
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            await LoadHealthAsync(ct);
            await LoadEventsAsync(ct);
            await LoadSnapshotAsync(force: true, ct);
            _report($"{Device.Name}: información actualizada.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadHealthAsync(CancellationToken ct)
    {
        try
        {
            var health = await _session.RequireApi().GetHealthAsync(Device.Id, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            HealthItems.Clear();
            foreach (var item in health.Items)
            {
                HealthItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write($"Estado de {Device.Name}: {ex.Message}");
            ActionError ??= "No se pudo cargar el estado del dispositivo: " + ex.Message;
        }
    }

    private async Task LoadEventsAsync(CancellationToken ct)
    {
        try
        {
            var events = await _session.RequireApi().GetEventsAsync(Device, 3, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            Events.Clear();
            foreach (var ringEvent in events)
            {
                Events.Add(new EventItemViewModel(this, ringEvent));
            }

            HasEvents = Events.Count > 0;
            OnPropertyChanged(nameof(EventCountLabel));

            foreach (var item in Events.ToList())
            {
                _ = LoadThumbnailAsync(item, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write($"Eventos de {Device.Name}: {ex.Message}");
            ActionError ??= "No se pudieron cargar los eventos: " + ex.Message;
        }
    }

    private async Task LoadThumbnailAsync(EventItemViewModel item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ThumbnailUrl))
        {
            return;
        }

        var cacheFile = Path.Combine(ThumbnailCacheFolder, PathUtil.SafeFileName(item.Id, "event") + ".jpg");

        try
        {
            await _thumbnailGate.WaitAsync(ct);
            try
            {
                byte[]? bytes = null;
                if (File.Exists(cacheFile))
                {
                    bytes = await File.ReadAllBytesAsync(cacheFile, ct);
                }

                if (bytes is null || bytes.Length == 0)
                {
                    bytes = await _session.RequireApi().DownloadBytesAsync(item.ThumbnailUrl!, ct);
                    if (bytes is { Length: > 0 })
                    {
                        Directory.CreateDirectory(ThumbnailCacheFolder);
                        await File.WriteAllBytesAsync(cacheFile, bytes, ct);
                    }
                }

                if (bytes is { Length: > 0 } && !ct.IsCancellationRequested)
                {
                    item.SetThumbnail(ImageUtil.FromBytes(bytes));
                }
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("Miniatura de evento: " + ex.Message);
        }
    }

    private async Task LoadSnapshotAsync(bool force, CancellationToken ct)
    {
        if (!Device.MotionDetectionEnabled)
        {
            SnapshotStatus = "Activa la detección de movimiento para ver instantáneas.";
            return;
        }

        try
        {
            if (force && DateTimeOffset.UtcNow - _lastForcedSnapshot < TimeSpan.FromSeconds(10))
            {
                force = false;
            }

            if (force)
            {
                _lastForcedSnapshot = DateTimeOffset.UtcNow;
            }

            var bytes = await _session.RequireApi().GetSnapshotAsync(Device.Id, force, ct);
            var image = ImageUtil.FromBytes(bytes);
            if (image is null || ct.IsCancellationRequested)
            {
                return;
            }

            SnapshotImage = image;
            SnapshotStatus = "Actualizada a las " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write($"Instantánea de {Device.Name}: {ex.Message}");
            if (force)
            {
                ActionError = "No se pudo obtener la instantánea: " + ex.Message;
            }
        }
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.SnapshotIntervalSeconds, 10, 600));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                if (IsLive || IsConnecting)
                {
                    continue;
                }

                await LoadSnapshotAsync(force: false, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Bucle de instantáneas", ex);
        }
    }

    [RelayCommand]
    private async Task ToggleLiveAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        if (IsLive)
        {
            await StopLiveAsync();
        }
        else
        {
            await StartLiveAsync();
        }
    }

    private async Task StartLiveAsync()
    {
        if (IsLive || IsConnecting || _disposed)
        {
            return;
        }

        if (!Device.IsOnline)
        {
            LiveError = "La cámara está sin conexión.";
            return;
        }

        if (Device.LiveViewDisabled)
        {
            LiveError = "La vista en vivo está desactivada en los ajustes de Ring.";
            return;
        }

        if (_vlc.LibVlc is null)
        {
            LiveError = "El reproductor de vídeo no está disponible: " + _vlc.InitError;
            return;
        }

        IsConnecting = true;
        LiveError = null;
        ActionError = null;
        LiveStatus = "Solicitando acceso al vídeo…";
        StreamStats = string.Empty;
        _lastSample = default;
        _lastSampleBytes = 0;

        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            // Ring accepts only one live session per camera. When the NVR is recording this
            // device, borrow its stream instead of asking Ring for a second one.
            var nvrRecorder = _findRecorder?.Invoke(Device.Id);
            var attach = PreviewAttachResult.NotAvailable;
            LivePreviewSource? preview = null;

            if (nvrRecorder is not null)
            {
                attach = nvrRecorder.AttachPreview(out preview);
            }

            if (attach == PreviewAttachResult.Busy)
            {
                LiveError = "El NVR está grabando un evento de esta cámara. Espera unos segundos e inténtalo de nuevo.";
                await StopLiveAsync();
                return;
            }

            if (attach is PreviewAttachResult.Shared or PreviewAttachResult.Reserved)
            {
                _previewRecorder = nvrRecorder;
                _sharedPreview = attach == PreviewAttachResult.Shared;
            }

            if (attach == PreviewAttachResult.Shared)
            {
                await StartSharedPreviewAsync(nvrRecorder!, preview, ct);
            }
            else
            {
                await StartDirectPreviewAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            await StopLiveAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Vídeo en directo", ex);
            LiveError = ex is TimeoutException
                ? "Ring no respondió a tiempo. Inténtalo de nuevo."
                : ex.Message;
            await StopLiveAsync();
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task StartDirectPreviewAsync(CancellationToken ct)
    {
        var stream = new RingLiveStream(_session.RequireApi(), Device,
            message => AppLog.Write($"[directo:{Device.Id}] {message}"));

        _stream = stream;
        stream.StatusChanged += OnStreamStatusChanged;
        stream.VideoPayload += OnVideoPayload;

        await stream.StartAsync(ct);

        if (string.IsNullOrWhiteSpace(stream.SdpFilePath))
        {
            throw new InvalidOperationException("Ring no devolvió una sesión de vídeo válida.");
        }

        AppLog.Write($"[directo:{Device.Id}] Abriendo el reproductor con {stream.SdpFilePath} (puerto {stream.LocalVideoPort}).");
        CreatePlayer(stream.SdpFilePath);
        stream.ForwardEnabled = true;
        IsLive = true;
        LiveStatus = "Esperando la imagen de la cámara…";
        _report($"{Device.Name}: vídeo en directo iniciado.");
    }

    /// <summary>
    /// Plays the stream that the NVR is already recording. This avoids a second Ring session,
    /// which the camera would reject, and keeps the recording uninterrupted.
    /// </summary>
    private async Task StartSharedPreviewAsync(DeviceRecorder recorder, LivePreviewSource? source, CancellationToken ct)
    {
        recorder.PreviewSourceChanged += OnPreviewSourceChanged;
        recorder.StatusChanged += OnPreviewRecorderStatus;

        if (source is null)
        {
            LiveStatus = "Esperando la señal del NVR…";
            source = await WaitForPreviewSourceAsync(recorder, ct);
        }

        CreatePlayer(source.SdpFilePath);
        AppLog.Write($"[directo:{Device.Id}] Compartiendo el SDP del NVR ({source.SdpFilePath}, puerto {source.Port}).");
        IsLive = true;
        LiveStatus = "Vídeo en directo (compartido con el NVR)";
        _report($"{Device.Name}: vídeo en directo desde el NVR.");
    }

    private async Task<LivePreviewSource> WaitForPreviewSourceAsync(DeviceRecorder recorder, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<LivePreviewSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LivePreviewSource> handler = (_, source) => tcs.TrySetResult(source);
        recorder.PreviewSourceChanged += handler;

        try
        {
            var current = recorder.PreviewSource;
            if (current is not null)
            {
                return current;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("El NVR no entregó la señal de vídeo a tiempo.")));
            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            recorder.PreviewSourceChanged -= handler;
        }
    }

    private void CreatePlayer(string sdpPath)
    {
        if (_vlc.LibVlc is null)
        {
            throw new InvalidOperationException("El reproductor de vídeo no está disponible: " + _vlc.InitError);
        }

        var media = new Media(_vlc.LibVlc, sdpPath, FromType.FromPath);
        media.AddOption(":demux=sdp");
        media.AddOption(":network-caching=250");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");

        var player = new VlcMediaPlayer(media) { EnableHardwareDecoding = false };
        player.EncounteredError += OnPlayerError;

        _media = media;
        _player = player;
        MediaPlayer = player;

        if (!player.Play())
        {
            throw new InvalidOperationException("El reproductor no pudo iniciar la reproducción.");
        }
    }

    private void DisposePlayer()
    {
        var player = _player;
        _player = null;

        if (player is not null)
        {
            player.EncounteredError -= OnPlayerError;

            try
            {
                player.Stop();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                player.Dispose();
            }
            catch
            {
                // Ignore.
            }
        }

        MediaPlayer = null;

        try
        {
            _media?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _media = null;
    }

    private async Task StopLiveAsync()
    {
        var previewRecorder = _previewRecorder;
        _previewRecorder = null;
        _sharedPreview = false;

        if (previewRecorder is not null)
        {
            previewRecorder.PreviewSourceChanged -= OnPreviewSourceChanged;
            previewRecorder.StatusChanged -= OnPreviewRecorderStatus;
            previewRecorder.EndPreview();
        }

        var stream = _stream;
        _stream = null;

        if (stream is not null)
        {
            stream.ForwardEnabled = false;
            stream.StatusChanged -= OnStreamStatusChanged;
            stream.VideoPayload -= OnVideoPayload;
        }

        DisposePlayer();

        if (stream is not null)
        {
            try
            {
                await stream.StopAsync();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                await stream.DisposeAsync();
            }
            catch
            {
                // Ignore.
            }
        }

        if (IsRecording)
        {
            await StopRecordingAsync(convert: true);
        }

        IsLive = false;
        IsConnecting = false;
        LiveStatus = "Sin vídeo en directo";
        StreamStats = string.Empty;
    }

    private void OnStreamStatusChanged(object? sender, LiveStreamStatusEventArgs e)
    {
        OnUi(() =>
        {
            LiveStatus = e.Message;

            if (e.State == LiveStreamState.Failed)
            {
                LiveError = e.Message;
                _ = StopLiveAsync();
            }
        });
    }

    private void OnPlayerError(object? sender, EventArgs e)
    {
        AppLog.Write("[directo] LibVLC notificó un error de reproducción.");
        OnUi(() =>
        {
            LiveError = "El reproductor encontró un problema al mostrar el vídeo.";
            _ = StopLiveAsync();
        });
    }

    /// <summary>The recorder reconnected: the SDP file and UDP port changed, so restart the player.</summary>
    private void OnPreviewSourceChanged(object? sender, LivePreviewSource source)
    {
        OnUi(() =>
        {
            if (!_sharedPreview || !IsLive || _disposed)
            {
                return;
            }

            try
            {
                DisposePlayer();
                CreatePlayer(source.SdpFilePath);
                LiveStatus = "Vídeo en directo (compartido con el NVR)";
            }
            catch (Exception ex)
            {
                AppLog.Error("Reanudar el vídeo compartido", ex);
                LiveError = "No se pudo reanudar el vídeo: " + ex.Message;
                _ = StopLiveAsync();
            }
        });
    }

    private void OnPreviewRecorderStatus(object? sender, DeviceRecorderStatus status)
    {
        OnUi(() =>
        {
            if (!_sharedPreview)
            {
                return;
            }

            switch (status.State)
            {
                case DeviceRecorderState.Connecting:
                case DeviceRecorderState.Backoff:
                    LiveStatus = "El NVR está reconectando la cámara…";
                    break;
                case DeviceRecorderState.Error:
                    LiveError = "El NVR dejó de recibir vídeo: " + status.Message;
                    _ = StopLiveAsync();
                    break;
                case DeviceRecorderState.Recording when IsLive:
                    LiveStatus = "Vídeo en directo (compartido con el NVR)";
                    break;
            }
        });
    }

    private void OnVideoPayload(byte[] payload, uint timestamp)
    {
        var recorder = _recorder;
        if (recorder is null)
        {
            return;
        }

        try
        {
            recorder.WritePayload(payload, timestamp);
        }
        catch (Exception ex)
        {
            AppLog.Write("Grabación H.264: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (IsRecording)
        {
            await StopRecordingAsync(convert: true);
            return;
        }

        if (!IsLive)
        {
            ActionError = "Inicia el vídeo en directo antes de grabar.";
            return;
        }

        try
        {
            var folder = Path.Combine(GetRecordingRoot(), PathUtil.SafeFileName(Device.Name, "camara"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.h264");

            var recorder = new H264RtpRecorder();
            recorder.Start(path);
            _recorder = recorder;

            IsRecording = true;
            RecordingInfo = "00:00";
            _report($"{Device.Name}: grabando el vídeo en directo…");
        }
        catch (Exception ex)
        {
            ActionError = "No se pudo iniciar la grabación: " + ex.Message;
        }
    }

    private async Task StopRecordingAsync(bool convert)
    {
        var recorder = _recorder;
        _recorder = null;

        if (recorder is null)
        {
            IsRecording = false;
            RecordingInfo = null;
            return;
        }

        var fps = recorder.FramesPerSecond;
        var path = recorder.Stop();
        recorder.Dispose();

        IsRecording = false;
        RecordingInfo = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        LastRecordingPath = path;

        if (!convert || _vlc.LibVlc is null)
        {
            _report("Grabación guardada: " + path);
            return;
        }

        IsConverting = true;
        _report("Convirtiendo la grabación a MP4…");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var mp4 = await _vlc.RemuxToMp4Async(path, fps, timeout.Token);

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Keep the raw file if it cannot be removed.
            }

            LastRecordingPath = mp4;
            _report("Grabación guardada: " + mp4);
        }
        catch (Exception ex)
        {
            AppLog.Error("Conversión de la grabación", ex);
            _report("Se guardó la grabación en formato H.264 (no se pudo crear el MP4): " + Path.GetFileName(path));
        }
        finally
        {
            IsConverting = false;
        }
    }

    [RelayCommand]
    private async Task SaveSnapshotAsync()
    {
        if (SnapshotImage is not BitmapSource source)
        {
            ActionError = "Todavía no hay una instantánea para guardar.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Guardar instantánea",
            Filter = "Imagen JPEG (*.jpg)|*.jpg",
            FileName = $"{PathUtil.SafeFileName(Device.Name, "camara")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jpg",
            InitialDirectory = GetSnapshotRoot(),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            await using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            _report("Instantánea guardada: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            ActionError = "No se pudo guardar la instantánea: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            var folder = GetRecordingRoot();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActionError = "No se pudo abrir la carpeta: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleLightAsync()
    {
        if (!Device.HasLight)
        {
            return;
        }

        var target = !LightOn;
        await RunActionAsync(
            () => _session.RequireApi().SetLightAsync(Device.Id, target),
            () => LightOn = target,
            target ? "encender la luz" : "apagar la luz");
    }

    [RelayCommand]
    private async Task ToggleSirenAsync()
    {
        if (!Device.HasSiren)
        {
            return;
        }

        var target = !SirenOn;
        await RunActionAsync(
            () => _session.RequireApi().SetSirenAsync(Device.Id, target),
            () => SirenOn = target,
            target ? "activar la sirena" : "desactivar la sirena");
    }

    [RelayCommand]
    private async Task ToggleMotionAsync()
    {
        var target = !MotionOn;
        await RunActionAsync(
            () => _session.RequireApi().SetMotionDetectionAsync(Device.Id, target),
            () =>
            {
                MotionOn = target;
                Device.MotionDetectionEnabled = target;
            },
            target ? "activar la detección de movimiento" : "desactivar la detección de movimiento");

        if (target)
        {
            _ = LoadSnapshotAsync(force: true, _cts?.Token ?? CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task ToggleChimeAsync()
    {
        var target = !ChimeOn;
        await RunActionAsync(
            () => _session.RequireApi().SetChimeAsync(Device.Id, target),
            () => ChimeOn = target,
            target ? "activar el timbre" : "desactivar el timbre");
    }

    [RelayCommand]
    private void DismissError() => ActionError = null;

    private async Task RunActionAsync(Func<Task> action, Action apply, string description)
    {
        try
        {
            ActionError = null;
            await action();
            apply();
            _report($"{Device.Name}: {description}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Acción " + description, ex);
            ActionError = $"No se pudo {description}: {ex.Message}";
        }
    }

    public async Task PlayEventAsync(EventItemViewModel item)
    {
        if (item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        item.Status = "Obteniendo grabación…";

        try
        {
            var url = await ResolveClipUrlAsync(item.Event);
            if (string.IsNullOrWhiteSpace(url))
            {
                item.Status = "Ring no tiene una grabación para este evento.";
                return;
            }

            item.Status = null;
            var window = new ClipWindow(_vlc, url, $"{item.KindLabel} · {item.TimeLabel}")
            {
                Owner = Application.Current?.MainWindow,
            };

            window.Show();
        }
        catch (Exception ex)
        {
            item.Status = "No se pudo abrir la grabación: " + ex.Message;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task SaveEventAsync(EventItemViewModel item)
    {
        if (item.IsBusy)
        {
            return;
        }

        var folder = GetRecordingRoot();
        Directory.CreateDirectory(folder);

        var dialog = new SaveFileDialog
        {
            Title = "Guardar grabación",
            Filter = "Vídeo MP4 (*.mp4)|*.mp4",
            FileName = $"{PathUtil.SafeFileName(Device.Name, "camara")}_{item.Event.CreatedAt.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{PathUtil.SafeFileName(item.KindLabel, "evento")}.mp4",
            InitialDirectory = folder,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        item.IsBusy = true;
        item.Status = "Obteniendo grabación…";

        try
        {
            var url = await ResolveClipUrlAsync(item.Event);
            if (string.IsNullOrWhiteSpace(url))
            {
                item.Status = "Ring no tiene una grabación para este evento.";
                return;
            }

            var progress = new Progress<double>(p => item.Status = $"Descargando… {p * 100:0}%");
            var ct = _cts?.Token ?? CancellationToken.None;
            await _session.RequireApi().DownloadFileAsync(url, dialog.FileName, progress, ct);

            item.Status = "Guardado";
            _report("Grabación guardada: " + dialog.FileName);
        }
        catch (OperationCanceledException)
        {
            item.Status = null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Descarga de grabación", ex);
            item.Status = "No se pudo guardar: " + ex.Message;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task<string?> ResolveClipUrlAsync(RingEvent ringEvent)
    {
        if (!string.IsNullOrWhiteSpace(ringEvent.PlaybackUrl))
        {
            return ringEvent.PlaybackUrl;
        }

        if (string.IsNullOrWhiteSpace(ringEvent.Id))
        {
            return null;
        }

        return await _session.RequireApi().GetClipUrlAsync(ringEvent.Id);
    }

    private void UpdateStats()
    {
        if (_sharedPreview && _previewRecorder is { IsRunning: false })
        {
            LiveError = "El NVR se detuvo y el vídeo en directo se cerró.";
            _ = StopLiveAsync();
        }

        var recorder = _recorder;
        if (recorder is { IsRecording: true })
        {
            var elapsed = recorder.Duration;
            RecordingInfo = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · {PathUtil.FormatBytes(recorder.BytesWritten)}";
        }

        var stream = _stream;
        if (stream is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var bytes = stream.ByteCount;

            if (_lastSample != default)
            {
                var seconds = (now - _lastSample).TotalSeconds;
                if (seconds > 0)
                {
                    var kbps = (bytes - _lastSampleBytes) * 8 / 1000.0 / seconds;
                    StreamStats = $"{kbps:0} kbps · {stream.Uptime:mm\\:ss}";
                }
            }

            _lastSample = now;
            _lastSampleBytes = bytes;
        }
    }

    private string GetRecordingRoot()
    {
        var folder = Environment.ExpandEnvironmentVariables(_settings.RecordingFolder);
        return string.IsNullOrWhiteSpace(folder) ? AppSettings.DefaultRecordingFolder : folder;
    }

    private static string GetSnapshotRoot()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        }

        return pictures;
    }

    private static string ThumbnailCacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RingControl", "thumbs");

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DeactivateAsync();

        _uiTimer.Stop();
        _thumbnailGate.Dispose();
    }
}
