using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RingControl.Setup;

public partial class MainWindow : Window
{
    private readonly bool _uninstall;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _completed;

    public MainWindow(bool uninstall)
    {
        InitializeComponent();

        _uninstall = uninstall;
        VersionText.Text = "v" + Installer.Version;
        HeaderSubtitle.Text = uninstall ? "Desinstalador" : "Instalador";
        InstallDirBox.Text = Installer.InstalledDir ?? InstallOptions.DefaultDir;

        if (uninstall)
        {
            InstallPanel.Visibility = Visibility.Collapsed;
            UninstallPanel.Visibility = Visibility.Visible;
            ActionButton.Content = "Desinstalar";

            var installed = Installer.IsInstalled;
            ActionButton.IsEnabled = installed;
            StatusText.Text = installed
                ? $"Instalado en {Installer.InstalledDir}"
                : "RingControl no está instalado en este equipo.";
        }
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Elige la carpeta de instalación",
            InitialDirectory = Directory.Exists(InstallDirBox.Text) ? InstallDirBox.Text : InstallOptions.DefaultDir,
        };

        if (dialog.ShowDialog(this) == true)
        {
            InstallDirBox.Text = Path.Combine(dialog.FolderName, "RingControl");
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_uninstall)
        {
            var confirm = MessageBox.Show(this,
                "¿Desinstalar RingControl de este equipo?\n\nLas grabaciones de vídeo se conservan.",
                "Desinstalar RingControl", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var options = new InstallOptions
        {
            InstallDir = InstallDirBox.Text.Trim(),
            DesktopShortcut = DesktopCheck.IsChecked == true,
            StartMenuShortcut = StartMenuCheck.IsChecked == true,
            AutostartNvr = AutostartCheck.IsChecked == true,
            AddToPath = PathCheck.IsChecked == true,
            PurgeData = PurgeCheck.IsChecked == true,
            LaunchApp = LaunchCheck.IsChecked == true,
        };

        await RunAsync(options);
    }

    private async Task RunAsync(InstallOptions options)
    {
        _busy = true;
        _cts = new CancellationTokenSource();

        InstallPanel.Visibility = Visibility.Collapsed;
        UninstallPanel.Visibility = Visibility.Collapsed;
        FinishPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;

        ProgressTitle.Text = _uninstall ? "Desinstalando RingControl…" : "Instalando RingControl…";
        ProgressText.Text = "Preparando…";
        ProgressBar.Value = 0;
        ProgressPercent.Text = "0 %";
        StatusText.Text = "No cierres esta ventana.";
        ActionButton.IsEnabled = false;
        CancelButton.Content = "Cancelar";

        var progress = new Progress<SetupProgress>(p =>
        {
            ProgressText.Text = p.Message;
            ProgressBar.Value = p.Percent;
            ProgressPercent.Text = $"{p.Percent:0} %";
        });

        try
        {
            if (_uninstall)
            {
                await Task.Run(() => Installer.UninstallAsync(options, progress, _cts.Token));
            }
            else
            {
                await Task.Run(() => Installer.InstallAsync(options, progress, _cts.Token));
            }

            _completed = true;
            ShowFinish(true, options, null);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            ResetToIdle();
            StatusText.Text = "Operación cancelada.";
        }
        catch (Exception ex)
        {
            ShowFinish(false, options, ex.Message);
        }
    }

    private void ShowFinish(bool success, InstallOptions options, string? error)
    {
        _busy = false;

        ProgressPanel.Visibility = Visibility.Collapsed;
        FinishPanel.Visibility = Visibility.Visible;
        ActionButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Visible;
        FinishButton.Focus();

        if (success)
        {
            FinishGlyph.Text = "✓";
            FinishGlyph.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            FinishTitle.Text = _uninstall ? "Desinstalación completada" : "Instalación completada";

            if (_uninstall)
            {
                FinishText.Text = options.PurgeData
                    ? "RingControl se ha eliminado, incluida la configuración y la sesión. Las grabaciones se conservan en Vídeos\\RingControl."
                    : "RingControl se ha eliminado. La configuración y las grabaciones se conservan.";

                LaunchCheck.Visibility = Visibility.Collapsed;
                StatusText.Text = "Desinstalación completada.";
            }
            else
            {
                FinishText.Text =
                    "RingControl está listo. Inicia sesión con tu cuenta de Ring; si quieres grabar sin abrir la ventana, activa el inicio automático del NVR al instalar o desde Ajustes.";

                LaunchCheck.Visibility = Visibility.Visible;
                StatusText.Text = $"Instalado en {options.InstallDir}";
            }
        }
        else
        {
            FinishGlyph.Text = "✕";
            FinishGlyph.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            FinishTitle.Text = _uninstall ? "No se pudo desinstalar" : "No se pudo instalar";
            FinishText.Text = error ?? "Se produjo un error inesperado.";
            LaunchCheck.Visibility = Visibility.Collapsed;
            StatusText.Text = "Error.";
        }
    }

    private void ResetToIdle()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        FinishPanel.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.Content = "Cancelar";
        FinishButton.Visibility = Visibility.Collapsed;

        if (_uninstall)
        {
            UninstallPanel.Visibility = Visibility.Visible;
        }
        else
        {
            InstallPanel.Visibility = Visibility.Visible;
        }
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_completed && !_uninstall && LaunchCheck.IsChecked == true)
        {
            try
            {
                Installer.Launch(Path.GetFullPath(InstallDirBox.Text.Trim()));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo abrir RingControl: " + ex.Message, "RingControl",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            StatusText.Text = "Cancelando…";
            _cts?.Cancel();
            return;
        }

        Close();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
