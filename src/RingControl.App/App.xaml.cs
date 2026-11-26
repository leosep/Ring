using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using RingControl.App.Services;
using RingControl.App.ViewModels;
using RingControl.App.Views;
using RingControl.Core.Auth;

namespace RingControl.App;

public partial class App : Application
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly TokenStore _tokenStore = new();

    private AppSettings _settings = null!;
    private RingSession _session = null!;
    private VlcService _vlc = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error("Excepción no controlada", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Tarea no observada", args.Exception);
            args.SetObserved();
        };

        AppLog.Trim();
        AppLog.Write("Ring Control iniciado.");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _settings = AppSettings.Load();
        _vlc = new VlcService();

        if (_vlc.InitError is not null)
        {
            AppLog.Write("LibVLC no está disponible: " + _vlc.InitError);
        }

        _session = CreateSession();

        try
        {
            var stored = _tokenStore.Load();
            if (stored is not null)
            {
                if (await _session.TryRestoreAsync(stored.RefreshToken))
                {
                    AppLog.Write("Sesión restaurada desde el token guardado.");
                    OpenMainWindow();
                    return;
                }

                _tokenStore.Clear();
            }

            ShowLogin();
        }
        catch (Exception ex)
        {
            AppLog.Error("Inicio de la aplicación", ex);
            MessageBox.Show(
                "No se pudo iniciar Ring Control.\n\n" + ex.Message,
                "Ring Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private RingSession CreateSession()
    {
        var session = new RingSession(_http, _settings.HardwareId);
        session.TokensUpdated += OnTokensUpdated;
        return session;
    }

    private void OnTokensUpdated(object? sender, RingTokens tokens)
    {
        _tokenStore.Save(tokens.RefreshToken, _settings.HardwareId, _settings.RememberedEmail);
    }

    private void ShowLogin()
    {
        var login = new LoginWindow(_session, _settings);
        if (login.ShowDialog() == true)
        {
            if (login.Tokens is not null)
            {
                _tokenStore.Save(login.Tokens.RefreshToken, _settings.HardwareId, _settings.RememberedEmail);
            }

            OpenMainWindow();
        }
        else
        {
            Shutdown();
        }
    }

    private void OpenMainWindow()
    {
        var viewModel = new MainViewModel(_session, _settings, _vlc, () => _settings.Save());
        viewModel.LogoutRequested += HandleLogout;
        viewModel.SettingsRequested += OpenSettings;

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        _ = viewModel.InitializeAsync();
    }

    private void HandleLogout()
    {
        var window = MainWindow;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow = null;

        if (window is MainWindow main)
        {
            _ = main.PrepareShutdownAsync();
            main.ForceClose();
        }
        else
        {
            window?.Close();
        }

        _tokenStore.Clear();

        _session.Dispose();
        _session = CreateSession();

        _settings.RememberedEmail = null;
        _settings.Save();

        ShowLogin();
    }

    private void OpenSettings()
    {
        if (MainWindow?.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var previousFolder = _settings.RecordingFolder;
        var window = new SettingsWindow(_settings) { Owner = MainWindow };

        if (window.ShowDialog() == true)
        {
            _settings.Save();
            _ = viewModel.SyncRecordingFolderAsync(previousFolder);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Excepción de interfaz", e.Exception);
        e.Handled = true;

        MessageBox.Show(
            "Se ha producido un error inesperado, pero la aplicación sigue abierta.\n\n" + e.Exception.Message,
            "Ring Control",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write("Ring Control cerrado.");

        _session?.Dispose();
        _vlc?.Dispose();
        _http.Dispose();

        base.OnExit(e);
    }
}
