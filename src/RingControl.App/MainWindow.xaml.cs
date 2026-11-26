using System.ComponentModel;
using System.Windows;
using RingControl.App.Services;
using RingControl.App.ViewModels;

namespace RingControl.App;

public partial class MainWindow : Window
{
    private Task? _shutdownTask;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        StateChanged += (_, _) => UpdateMaximizeButton();
    }

    /// <summary>
    /// Keeps the window inside the usable desktop. At 125 % scaling a 1080p screen has
    /// roughly 832 DIP of height, so a fixed 880 DIP window would open partly off-screen.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        MaxWidth = area.Width;
        MaxHeight = area.Height;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);

        AppLog.Write($"Window: work area {area.Width:0}×{area.Height:0} DIP, " +
                     $"initial size {Width:0}×{Height:0} DIP.");
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Stops the live stream and recording. Safe to call more than once.</summary>
    public Task PrepareShutdownAsync()
    {
        if (_shutdownTask is not null)
        {
            return _shutdownTask;
        }

        _shutdownTask = DataContext is MainViewModel viewModel
            ? viewModel.DisposeAsync().AsTask()
            : Task.CompletedTask;

        return _shutdownTask;
    }

    /// <summary>Closes the window without waiting for the background cleanup (used on logout).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _ = CloseAfterCleanupAsync();
            return;
        }

        base.OnClosing(e);
    }

    private async Task CloseAfterCleanupAsync()
    {
        try
        {
            await PrepareShutdownAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Window close", ex);
        }

        _allowClose = true;
        Close();
    }
}
