namespace RingControl.Core.Platform;

/// <summary>
/// Per-user folders used by the CLI and the GUI. On Linux this follows the XDG base
/// directory spec so the daemon works under systemd with a normal home directory.
/// </summary>
public static class PlatformPaths
{
    public static string ConfigDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RingControl")
        : Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "ringcontrol");

    public static string DataDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RingControl")
        : Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "ringcontrol");

    public static string LogsDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(DataDirectory, "logs")
        : Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(".local", "state")), "ringcontrol", "logs");

    public static string DefaultNvrConfigFile => Path.Combine(ConfigDirectory, "nvr.json");

    public static string SessionFile => Path.Combine(ConfigDirectory, "session.dat");

    private static string Xdg(string envVar, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, fallback);
    }
}
