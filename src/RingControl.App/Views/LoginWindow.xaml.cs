using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RingControl.App.Services;
using RingControl.App.ViewModels;
using RingControl.Core.Auth;

namespace RingControl.App.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(RingSession session, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = new LoginViewModel(session, settings);
        DataContext = _viewModel;

        _viewModel.Succeeded += OnSucceeded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        PasswordInput.PasswordChanged += (_, _) => _viewModel.Password = PasswordInput.Password;
        Loaded += (_, _) => EmailInput.Focus();
    }

    public RingTokens? Tokens => _viewModel.Tokens;

    private void OnSucceeded()
    {
        DialogResult = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.IsTwoFactor) && _viewModel.IsTwoFactor)
        {
            Dispatcher.BeginInvoke(() =>
            {
                CodeInput.Focus();
                Keyboard.Focus(CodeInput);
            });
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse button was already released.
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnManageAccount(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://account.ring.com") { UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is best effort.
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
