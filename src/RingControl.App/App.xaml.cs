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

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private AppSettings _settings = null!;
    private RingSession _session = null!;
    private VlcService _vlc = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prevent a second GUI window when the app is launched twice (for example a
        // manual launch while an instance opened from the installer is still running).
        if (!AcquireSingleInstance())
        {
            MessageBox.Show(
                "RingControl is already running.",
                "RingControl",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error("Unhandled exception", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };

        AppLog.Trim();
        AppLog.Write("Ring Control started.");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _settings = AppSettings.Load();
        _vlc = new VlcService();

        if (_vlc.InitError is not null)
        {
            AppLog.Write("LibVLC is not available: " + _vlc.InitError);
        }

        _session = CreateSession();

        try
        {
            var stored = _tokenStore.Load();
            if (stored is not null)
            {
                if (await _session.TryRestoreAsync(stored.RefreshToken))
                {
                    AppLog.Write("Session restored from the saved token.");
                    OpenMainWindow();
                    return;
                }

                _tokenStore.Clear();
            }

            ShowLogin();
        }
        catch (Exception ex)
        {
            AppLog.Error("Application startup", ex);
            MessageBox.Show(
                "Could not start Ring Control.\n\n" + ex.Message,
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

    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: false, @"Local\RingControl.Desktop");

        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed without releasing the mutex; take ownership.
            _ownsInstanceMutex = true;
        }

        return _ownsInstanceMutex;
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

    private bool _loggingOut;

    private async void HandleLogout()
    {
        if (_loggingOut)
        {
            return;
        }

        _loggingOut = true;

        var window = MainWindow;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow = null;

        try
        {
            if (window is MainWindow main)
            {
                // Finish streams, recordings and the NVR before the API client is disposed;
                // otherwise those components keep using a closed session.
                await main.PrepareShutdownAsync();
                main.ForceClose();
            }
            else
            {
                window?.Close();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Sign out", ex);
        }

        _tokenStore.Clear();

        _session.Dispose();
        _session = CreateSession();

        _settings.RememberedEmail = null;
        _settings.Save();

        _loggingOut = false;
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
        AppLog.Error("UI exception", e.Exception);
        e.Handled = true;

        MessageBox.Show(
            "An unexpected error occurred, but the application is still running.\n\n" + e.Exception.Message,
            "Ring Control",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write("Ring Control closed.");

        _session?.Dispose();
        _vlc?.Dispose();
        _http.Dispose();

        if (_ownsInstanceMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
                // Ignore: the process is exiting anyway.
            }
        }

        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
