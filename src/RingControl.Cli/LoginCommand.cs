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
            email = Ui.Prompt("Correo de Ring");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new CliException("Falta el correo de Ring (--email o RING_EMAIL).");
        }

        var password = args.GetOrEnv("password", "RING_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            password = Console.IsInputRedirected
                ? Console.ReadLine()?.Trim()
                : Ui.PromptSecret("Contraseña de Ring");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new CliException("Falta la contraseña (--password o RING_PASSWORD).");
        }

        var code = args.GetOrEnv("code", "RING_2FA_CODE");

        Ui.Header("Iniciar sesión en Ring");

        using var session = CliSession.Login(email, password, code);
        var devices = session.GetDevices();

        Ui.Blank();
        Ui.Success($"Sesión iniciada como {email}.");
        Ui.Info($"Sesión guardada en {PlatformPaths.SessionFile}");
        Ui.Info($"ID de equipo: {session.HardwareId}");

        if (devices.Count > 0)
        {
            Ui.Blank();
            Ui.Header($"Cámaras ({devices.Count})");
            DevicesCommand.PrintDevices(devices);
        }
        else
        {
            Ui.Warn("La cuenta no tiene cámaras ni timbres Ring.");
        }

        Ui.Blank();
        Ui.Info("Siguiente paso: ringcontrol nvr config --init  y  ringcontrol nvr run");
        Ui.Blank();

        return Task.FromResult(0);
    }
}
