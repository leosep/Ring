using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RingControl.App.Services;

namespace RingControl.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;
        FolderBox.Text = settings.RecordingFolder;
        AutoStartCheck.IsChecked = settings.AutoStartLive;
        NvrAutoStartCheck.IsChecked = settings.NvrAutoStart;

        var interval = Math.Clamp(settings.SnapshotIntervalSeconds, 10, 600);
        IntervalSlider.Value = interval;
        IntervalText.Text = $"{interval} s";
        IntervalSlider.ValueChanged += (_, e) => IntervalText.Text = $"{(int)e.NewValue} s";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the recordings folder",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : AppSettings.DefaultRecordingFolder,
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = Environment.ExpandEnvironmentVariables(FolderBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AppSettings.DefaultRecordingFolder;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "That folder cannot be used:\n\n" + ex.Message,
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _settings.RecordingFolder = folder;
        _settings.AutoStartLive = AutoStartCheck.IsChecked == true;
        _settings.NvrAutoStart = NvrAutoStartCheck.IsChecked == true;
        _settings.SnapshotIntervalSeconds = (int)IntervalSlider.Value;
        _settings.Save();

        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore if the mouse button was released.
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
