using System.Text;

namespace RingControl.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (OperatingSystem.IsWindows() && args.Contains("--hidden", StringComparer.OrdinalIgnoreCase))
        {
            HideConsoleWindow();
        }

        return await Cli.RunAsync(args).ConfigureAwait(false);
    }

    /// <summary>
    /// Used by the autostart shortcut so the NVR daemon runs without a console window.
    /// </summary>
    private static void HideConsoleWindow()
    {
        try
        {
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, 0);
            }
        }
        catch
        {
            // Hiding the console is cosmetic; never block startup.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine($"RingControl {Version}");
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "login" => await LoginCommand.RunAsync(new CliArgs(rest, "verbose")).ConfigureAwait(false),
                "logout" => Logout(),
                "devices" or "list" => await DevicesCommand.RunAsync(new CliArgs(rest, "json")).ConfigureAwait(false),
                "status" => await DevicesCommand.StatusAsync(new CliArgs(rest, "json")).ConfigureAwait(false),
                "snapshot" or "snap" => await MediaCommands.SnapshotAsync(new CliArgs(rest)).ConfigureAwait(false),
                "record" or "clip" => await MediaCommands.RecordAsync(new CliArgs(rest)).ConfigureAwait(false),
                "clips" or "recordings" => await MediaCommands.ClipsAsync(new CliArgs(rest, "json", "prune")).ConfigureAwait(false),
                "nvr" => await NvrCommand.RunAsync(rest).ConfigureAwait(false),
                _ => Unknown(command),
            };
        }
        catch (CliException ex)
        {
            Ui.Error(ex.Message);
            return ex.ExitCode;
        }
        catch (RingControl.Core.Api.RingApiException ex)
        {
            Ui.Error("Ring devolvió un error: " + ex.Message);
            if (Environment.GetEnvironmentVariable("RINGCONTROL_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("Operación cancelada.");
            return 130;
        }
        catch (Exception ex)
        {
            Ui.Error("Error inesperado: " + ex.Message);

            if (Environment.GetEnvironmentVariable("RINGCONTROL_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }

    public static string Version => typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static int Unknown(string command)
    {
        Ui.Error($"Comando desconocido: {command}");
        PrintHelp();
        return 2;
    }

    private static int Logout()
    {
        var store = new RingControl.Core.Security.SessionStore();
        store.Clear();
        Ui.Success("Sesión eliminada de este equipo.");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Ui.Line($"RingControl {Version} · NVR y cliente para Ring (API no oficial)");
        Console.WriteLine();
        Ui.Line("Uso: ringcontrol <comando> [opciones]");
        Console.WriteLine();
        Ui.Line("Comandos:");
        Ui.Line("  login                Inicia sesión en Ring (guarda solo el refresh token)");
        Ui.Line("  logout               Borra la sesión guardada");
        Ui.Line("  devices              Lista cámaras y timbres (--json)");
        Ui.Line("  status               Estado de la cuenta y del NVR");
        Ui.Line("  snapshot [cámara]    Guarda una instantánea JPEG (--out archivo.jpg)");
        Ui.Line("  record [cámara]      Graba un clip ahora (--seconds 30, --root carpeta)");
        Ui.Line("  clips                Lista grabaciones locales (--prune, --json)");
        Console.WriteLine();
        Ui.Line("NVR:");
        Ui.Line("  nvr run              Arranca el NVR en primer plano");
        Ui.Line("  nvr status           Muestra el estado del NVR (--json)");
        Ui.Line("  nvr stop             Detiene el NVR en ejecución");
        Ui.Line("  nvr config           Muestra la configuración (--init la crea, --json)");
        Console.WriteLine();
        Ui.Line("Opciones de nvr run:");
        Ui.Line("  --config <archivo>   Configuración alternativa (por defecto nvr.json)");
        Ui.Line("  --root <carpeta>     Carpeta de grabaciones");
        Ui.Line("  --mode events|continuous|both");
        Ui.Line("  --device <id,...>    Limita el NVR a esas cámaras");
        Ui.Line("  --segment <min>      Duración de cada segmento continuo");
        Ui.Line("  --clip <seg>         Duración de los clips por evento");
        Ui.Line("  --retention <días>   Retención (0 = sin límite de días)");
        Ui.Line("  --max-gb <GB>        Cuota de disco (0 = sin límite)");
        Console.WriteLine();
        Ui.Line("Cámara: ID o parte del nombre. Con una sola cámara se usa automáticamente.");
        Ui.Line("Variables: RING_EMAIL, RING_PASSWORD, RING_2FA_CODE, RINGCONTROL_DEBUG=1");
        Console.WriteLine();
    }
}
