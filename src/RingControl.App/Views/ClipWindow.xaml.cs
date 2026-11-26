using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using RingControl.App.Services;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RingControl.App.Views;

public partial class ClipWindow : Window
{
    private readonly VlcService _vlc;
    private readonly string _source;
    private Media? _media;
    private VlcMediaPlayer? _player;
    private bool _updating;
    private bool _started;

    public ClipWindow(VlcService vlc, string source, string title)
    {
        InitializeComponent();

        _vlc = vlc;
        _source = source;
        TitleText.Text = title;

        Loaded += (_, _) => StartPlayback();
        Closed += (_, _) => Cleanup();
        KeyDown += OnKeyDown;
        StateChanged += (_, _) => UpdateMaximizeButton();
    }

    private void StartPlayback()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        if (_vlc.LibVlc is null)
        {
            ShowStatus("The video player is not available: " + _vlc.InitError, isError: true);
            return;
        }

        try
        {
            var remote = Uri.TryCreate(_source, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            _media = new Media(_vlc.LibVlc, _source, remote ? FromType.FromLocation : FromType.FromPath);
            _media.AddOption(":network-caching=1000");

            _player = new VlcMediaPlayer(_media);
            _player.TimeChanged += (_, e) => OnUi(() =>
            {
                _updating = true;
                SeekBar.Value = _player?.Position ?? 0;
                _updating = false;
                TimeText.Text = Format(e.Time);
            });
            _player.LengthChanged += (_, e) => OnUi(() =>
            {
                DurationText.Text = e.Length > 0 ? Format(e.Length) : "--:--";
                SeekBar.IsEnabled = e.Length > 0;
            });
            _player.Playing += (_, _) => OnUi(() =>
            {
                PlayButton.Content = "\uE769";
                ShowStatus(null, isError: false);
            });
            _player.Paused += (_, _) => OnUi(() => PlayButton.Content = "\uE768");
            _player.Stopped += (_, _) => OnUi(() => PlayButton.Content = "\uE768");
            _player.EndReached += (_, _) => OnUi(() =>
            {
                PlayButton.Content = "\uE768";
                SeekBar.Value = 0;
            });
            _player.EncounteredError += (_, _) => OnUi(() =>
                ShowStatus("Could not play the recording.", isError: true));

            Video.MediaPlayer = _player;

            // The native surface must exist before Play(), otherwise LibVLC opens a
            // separate top-level window instead of drawing inside the app.
            if (_player.Hwnd == IntPtr.Zero)
            {
                Video.UpdateLayout();
                Video.MediaPlayer = null;
                Video.MediaPlayer = _player;
            }

            if (!_player.Play())
            {
                ShowStatus("Could not start playback.", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open the recording: " + ex.Message, isError: true);
        }
    }

    private void ShowStatus(string? text, bool isError)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5))
            : (System.Windows.Media.Brush)FindResource("TextMutedBrush");
    }

    private static string Format(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

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

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _player is null)
        {
            return;
        }

        if (_player.IsSeekable)
        {
            _player.Position = (float)Math.Clamp(e.NewValue, 0, 1);
        }
    }

    private void VolumeBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
        {
            _player.Volume = (int)e.NewValue;
        }
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                PlayPause_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                if (_player is not null)
                {
                    _player.Time = Math.Max(0, _player.Time - 5000);
                }

                break;
            case Key.Right:
                if (_player is not null)
                {
                    _player.Time = Math.Min(_player.Length, _player.Time + 5000);
                }

                break;
        }
    }

    private void Cleanup()
    {
        try
        {
            Video.MediaPlayer = null;
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _player?.Stop();
        }
        catch
        {
            // Ignore.
        }

        _player?.Dispose();
        _player = null;

        _media?.Dispose();
        _media = null;
    }
}
