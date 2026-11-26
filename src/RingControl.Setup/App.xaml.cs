using System.IO;
using System.Windows;

namespace RingControl.Setup;

public partial class App : Application
{
    public static SetupArgs Args { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = SetupArgs.Parse(e.Args);

        if (Args.Silent)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSilentAsync();
            return;
        }

        var window = new MainWindow(Args.Uninstall);
        MainWindow = window;
        window.Show();
    }

    private async Task RunSilentAsync()
    {
        try
        {
            if (Args.Uninstall)
            {
                await Installer.UninstallAsync(Args.ToOptions(), new Progress<SetupProgress>(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await Installer.InstallAsync(Args.ToOptions(), new Progress<SetupProgress>(), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            Shutdown(0);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "RingControlSetup-error.log"), ex.ToString());
            }
            catch
            {
                // Nothing else we can do.
            }

            Shutdown(1);
        }
    }
}

/// <summary>Command line accepted by the installer, including the silent/unattended mode.</summary>
public sealed class SetupArgs
{
    public bool Silent { get; private set; }

    public bool Uninstall { get; private set; }

    public bool PurgeData { get; private set; }

    public bool NoLaunch { get; private set; }

    public bool DesktopShortcut { get; private set; } = true;

    public bool StartMenuShortcut { get; private set; } = true;

    public bool AutostartNvr { get; private set; }

    public bool AddToPath { get; private set; } = true;

    public string? Directory { get; private set; }

    public static SetupArgs Parse(string[] args)
    {
        var result = new SetupArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "/s":
                case "--silent":
                case "/silent":
                case "/verysilent":
                    result.Silent = true;
                    break;
                case "--uninstall":
                case "/uninstall":
                    result.Uninstall = true;
                    break;
                case "--purge":
                case "/purge":
                    result.PurgeData = true;
                    break;
                case "--no-launch":
                    result.NoLaunch = true;
                    break;
                case "--no-desktop":
                    result.DesktopShortcut = false;
                    break;
                case "--no-startmenu":
                    result.StartMenuShortcut = false;
                    break;
                case "--autostart":
                case "--nvr-autostart":
                    result.AutostartNvr = true;
                    break;
                case "--no-path":
                    result.AddToPath = false;
                    break;
                case "--dir":
                case "/dir":
                case "/d":
                    if (i + 1 < args.Length)
                    {
                        result.Directory = args[++i];
                    }

                    break;
                default:
                    if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Directory = arg["--dir=".Length..].Trim('"');
                    }
                    else if (arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Directory = arg["/DIR=".Length..].Trim('"');
                    }
                    else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Directory = arg["/D=".Length..].Trim('"');
                    }

                    break;
            }
        }

        return result;
    }

    public InstallOptions ToOptions() => new()
    {
        InstallDir = string.IsNullOrWhiteSpace(Directory) ? InstallOptions.DefaultDir : Directory!,
        DesktopShortcut = DesktopShortcut,
        StartMenuShortcut = StartMenuShortcut,
        AutostartNvr = AutostartNvr,
        AddToPath = AddToPath,
        LaunchApp = false,
        PurgeData = PurgeData,
    };
}
