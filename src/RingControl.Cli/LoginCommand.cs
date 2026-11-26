using RingControl.Core.Platform;
using RingControl.Core.Security;

namespace RingControl.Cli;

internal static class LoginCommand
{
    public static Task<int> RunAsync(CliArgs args)
    {
        var email = args.GetOrEnv("email", "RING_EMAIL");
        if (string.IsNullOrWhiteSpace(email))
        {
            email = Ui.Prompt("Ring email");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new CliException("Missing the Ring email (--email or RING_EMAIL).");
        }

        var password = args.GetOrEnv("password", "RING_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            password = Console.IsInputRedirected
                ? Console.ReadLine()?.Trim()
                : Ui.PromptSecret("Ring password");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new CliException("Missing the password (--password or RING_PASSWORD).");
        }

        var code = args.GetOrEnv("code", "RING_2FA_CODE");

        Ui.Header("Sign in to Ring");

        using var session = CliSession.Login(email, password, code);
        var devices = session.GetDevices();

        Ui.Blank();
        Ui.Success($"Signed in as {email}.");
        Ui.Info($"Session saved to {PlatformPaths.SessionFile}");
        Ui.Info($"Device ID: {session.HardwareId}");

        if (devices.Count > 0)
        {
            Ui.Blank();
            Ui.Header($"Cameras ({devices.Count})");
            DevicesCommand.PrintDevices(devices);
        }
        else
        {
            Ui.Warn("The account has no Ring cameras or doorbells.");
        }

        Ui.Blank();
        Ui.Info("Next step: ringcontrol nvr config --init  and  ringcontrol nvr run");
        Ui.Blank();

        return Task.FromResult(0);
    }
}
