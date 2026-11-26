using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingControl.App.Services;
using RingControl.Core.Auth;

namespace RingControl.App.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly RingSession _session;
    private readonly AppSettings _settings;

    public LoginViewModel(RingSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        Email = settings.RememberedEmail ?? string.Empty;
        RememberEmail = settings.RememberEmail;
    }

    [ObservableProperty]
    private string email;

    /// <summary>Set from the PasswordBox in the window (PasswordBox.Password is not bindable).</summary>
    public string Password { get; set; } = string.Empty;

    [ObservableProperty]
    private string twoFactorCode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCredentials))]
    [NotifyPropertyChangedFor(nameof(SubmitText))]
    private bool isTwoFactor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitText))]
    private bool isBusy;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string? infoMessage;

    [ObservableProperty]
    private bool rememberEmail;

    public bool ShowCredentials => !IsTwoFactor;

    public string SubmitText => IsBusy
        ? "Connecting…"
        : IsTwoFactor
            ? "Verify code"
            : "Sign in";

    public RingTokens? Tokens { get; private set; }

    public event Action? Succeeded;

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;

        if (!IsTwoFactor)
        {
            if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
            {
                ErrorMessage = "Enter a valid email address.";
                return;
            }

            if (string.IsNullOrEmpty(Password))
            {
                ErrorMessage = "Enter your Ring password.";
                return;
            }
        }
        else if (TwoFactorCode.Trim().Length < 4)
        {
            ErrorMessage = "Enter the verification code Ring sent you.";
            return;
        }

        IsBusy = true;

        try
        {
            var tokens = await _session.LoginAsync(
                Email.Trim(),
                Password,
                IsTwoFactor ? TwoFactorCode.Trim() : null);

            Tokens = tokens;

            _settings.RememberEmail = RememberEmail;
            _settings.RememberedEmail = RememberEmail ? Email.Trim() : null;
            _settings.Save();

            InfoMessage = null;
            Succeeded?.Invoke();
        }
        catch (RingAuthException ex) when (ex.RequiresTwoFactor)
        {
            IsTwoFactor = true;
            InfoMessage = ex.Prompt ?? "Ring requires a verification code.";

            // Keep the password: Ring asks for it again together with the code on
            // the second login request.
            TwoFactorCode = string.Empty;

            if (ex.StatusCode == 400)
            {
                ErrorMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BackToCredentials()
    {
        IsTwoFactor = false;
        TwoFactorCode = string.Empty;
        ErrorMessage = null;
        InfoMessage = null;
    }
}
