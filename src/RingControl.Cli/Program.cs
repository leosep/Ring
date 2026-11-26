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
            Ui.Error("Ring returned an error: " + ex.Message);
            if (Environment.GetEnvironmentVariable("RINGCONTROL_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("Operation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Ui.Error("Unexpected error: " + ex.Message);

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
        Ui.Error($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static int Logout()
    {
        var store = new RingControl.Core.Security.SessionStore();
        store.Clear();
        Ui.Success("Session removed from this computer.");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Ui.Line($"RingControl {Version} · NVR and Ring client (unofficial API)");
        Console.WriteLine();
        Ui.Line("Usage: ringcontrol <command> [options]");
        Console.WriteLine();
        Ui.Line("Commands:");
        Ui.Line("  login                Sign in to Ring (stores only the refresh token)");
        Ui.Line("  logout               Delete the saved session");
        Ui.Line("  devices              List cameras and doorbells (--json)");
        Ui.Line("  status               Account and NVR status");
        Ui.Line("  snapshot [camera]    Save a JPEG snapshot (--out file.jpg)");
        Ui.Line("  record [camera]      Record a clip now (--seconds 30, --root folder)");
        Ui.Line("  clips                List local recordings (--prune, --json)");
        Console.WriteLine();
        Ui.Line("NVR:");
        Ui.Line("  nvr run              Start the NVR in the foreground");
        Ui.Line("  nvr status           Show the NVR status (--json)");
        Ui.Line("  nvr stop             Stop the running NVR");
        Ui.Line("  nvr config           Show the configuration (--init creates it, --json)");
        Console.WriteLine();
        Ui.Line("nvr run options:");
        Ui.Line("  --config <file>      Alternative configuration (default nvr.json)");
        Ui.Line("  --root <folder>      Recordings folder");
        Ui.Line("  --mode events|continuous|both");
        Ui.Line("  --device <id,...>    Limit the NVR to those cameras");
        Ui.Line("  --segment <min>      Duration of each continuous segment");
        Ui.Line("  --clip <sec>         Duration of event clips");
        Ui.Line("  --retention <days>   Retention (0 = no day limit)");
        Ui.Line("  --max-gb <GB>        Disk quota (0 = no limit)");
        Console.WriteLine();
        Ui.Line("Camera: ID or part of the name. With a single camera it is used automatically.");
        Ui.Line("Variables: RING_EMAIL, RING_PASSWORD, RING_2FA_CODE, RINGCONTROL_DEBUG=1");
        Console.WriteLine();
    }
}
