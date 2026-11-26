using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RingControl.Setup;

public sealed class InstallOptions
{
    public string InstallDir { get; set; } = DefaultDir;

    public bool DesktopShortcut { get; set; } = true;

    public bool StartMenuShortcut { get; set; } = true;

    public bool AutostartNvr { get; set; }

    public bool AddToPath { get; set; } = true;

    public bool LaunchApp { get; set; } = true;

    public bool PurgeData { get; set; }

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "RingControl");
}

public sealed record SetupProgress(string Message, double Percent);

/// <summary>
/// Installs and removes RingControl for the current Windows user (no administrator rights
/// needed). The payload is a zip embedded in this executable: the desktop GUI plus the
/// self-contained CLI/NVR.
/// </summary>
public static class Installer
{
    public const string DisplayName = "RingControl";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RingControl";
    private const string SettingsKey = @"Software\RingControl";
    private const string EnvironmentKey = @"Environment";

    private const int HwndBroadcast = 0xFFFF;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string? InstalledDir
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                var dir = key?.GetValue("InstallDir") as string;

                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return dir;
                }
            }
            catch
            {
                // Fall through to the default probe.
            }

            return null;
        }
    }

    public static bool IsInstalled => !string.IsNullOrWhiteSpace(InstalledDir) &&
                                      File.Exists(Path.Combine(InstalledDir!, "RingControl.exe"));

    public static string GuiPath(string installDir) => Path.Combine(installDir, "RingControl.exe");

    public static string CliPath(string installDir) => Path.Combine(installDir, "cli", "ringcontrol.exe");

    /// <summary>Same default as the GUI and the NVR (Videos\RingControl).</summary>
    private static string DefaultRecordingsFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        }

        return Path.Combine(videos, "RingControl");
    }

    public static async Task InstallAsync(InstallOptions options, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The installer only works on Windows.");
        }

        var installDir = NormalizeInstallDir(options.InstallDir);

        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                            ?? throw new InvalidOperationException(
                                "This installer was built without content. Download the full installer.");

        progress.Report(new SetupProgress("Checking the installation…", 2));
        await Task.Run(CloseRunningInstances, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var staging = installDir.TrimEnd(Path.DirectorySeparatorChar) + ".staging";

        try
        {
            TryDeleteDirectory(staging, 0);
            Directory.CreateDirectory(staging);

            ExtractPayload(payload, staging, progress, ct);

            progress.Report(new SetupProgress("Installing files…", 70));
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(installDir))
            {
                TryDeleteDirectory(installDir, 2);
            }

            var parent = Path.GetDirectoryName(installDir);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(installDir))
            {
                // The running setup may live inside the install folder (reinstall or
                // upgrade), so Windows cannot delete that folder. Merge the new files
                // over the existing ones instead of failing the installation.
                CopyDirectory(staging, installDir);
                TryDeleteDirectory(staging, 0);
            }
            else
            {
                Directory.Move(staging, installDir);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDeleteDirectory(staging, 0);
            throw new InvalidOperationException(
                $"Cannot write to \"{installDir}\". Choose a folder inside your user profile.", ex);
        }
        catch
        {
            TryDeleteDirectory(staging, 0);
            throw;
        }

        ct.ThrowIfCancellationRequested();

        progress.Report(new SetupProgress("Preparing the uninstaller…", 76));
        CopySelfInto(installDir);

        progress.Report(new SetupProgress("Creating shortcuts…", 82));
        CreateShortcuts(installDir, options);

        progress.Report(new SetupProgress("Configuring autostart…", 88));
        ConfigureAutostart(installDir, options.AutostartNvr);

        progress.Report(new SetupProgress("Updating PATH…", 92));
        if (options.AddToPath)
        {
            AddToPath(Path.Combine(installDir, "cli"));
        }
        else
        {
            RemoveFromPath(Path.Combine(installDir, "cli"));
        }

        progress.Report(new SetupProgress("Registering the application…", 96));
        WriteRegistry(installDir);

        progress.Report(new SetupProgress("Installation completed.", 100));
    }

    public static async Task UninstallAsync(InstallOptions options, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The uninstaller only works on Windows.");
        }

        var installDir = !string.IsNullOrWhiteSpace(options.InstallDir)
            ? NormalizeInstallDir(options.InstallDir)
            : InstalledDir ?? InstallOptions.DefaultDir;

        progress.Report(new SetupProgress("Closing applications…", 5));
        await Task.Run(CloseRunningInstances, ct).ConfigureAwait(false);

        progress.Report(new SetupProgress("Removing shortcuts…", 20));
        DeleteShortcuts(installDir);
        ConfigureAutostart(installDir, enabled: false);

        progress.Report(new SetupProgress("Updating PATH…", 35));
        RemoveFromPath(Path.Combine(installDir, "cli"));

        progress.Report(new SetupProgress("Removing the registry entries…", 50));
        DeleteRegistry();

        if (options.PurgeData)
        {
            progress.Report(new SetupProgress("Removing configuration and session…", 60));
            PurgeConfiguration();
        }

        progress.Report(new SetupProgress("Removing files…", 70));
        ct.ThrowIfCancellationRequested();

        var self = Environment.ProcessPath;
        var selfInsideInstallDir = !string.IsNullOrWhiteSpace(self) &&
                                   Path.GetFullPath(self).StartsWith(
                                       installDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase);

        if (selfInsideInstallDir)
        {
            // The running executable lives inside the folder: let cmd remove everything after exit.
            ScheduleDirectoryRemoval(installDir);
        }
        else
        {
            TryDeleteDirectory(installDir, 3);
        }

        progress.Report(new SetupProgress("Uninstallation completed.", 100));
    }

    public static void Launch(string installDir)
    {
        var exe = GuiPath(installDir);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("RingControl.exe was not found.", exe);
        }

        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = installDir,
        });
    }

    private static string NormalizeInstallDir(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Specify an installation folder.");
        }

        var full = Path.GetFullPath(value.Trim().Trim('"'));
        var root = Path.GetPathRoot(full);

        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot install to the root of a drive.");
        }

        if (full.Length < 12)
        {
            throw new InvalidOperationException("The installation path is too short. Choose a folder such as C:\\Users\\YourUser\\Programs\\RingControl.");
        }

        return full;
    }

    private static void ExtractPayload(Stream payload, string destination, IProgress<SetupProgress> progress,
        CancellationToken ct)
    {
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var total = Math.Max(entries.Count, 1);
        var done = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, relative));

            if (!target.StartsWith(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installation package contains an invalid path.");
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(target, overwrite: true);
            done++;

            if (done % 25 == 0 || done == entries.Count)
            {
                var percent = 5 + done * 63.0 / total;
                progress.Report(new SetupProgress($"Installing files… ({done}/{entries.Count})", percent));
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CopySelfInto(string installDir)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self))
        {
            return;
        }

        var target = Path.Combine(installDir, "RingControlSetup.exe");
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Copy(self, target, overwrite: true);
        }
        catch
        {
            // Without a local copy the user can still uninstall from Settings if the original exists.
        }
    }

    private static void CreateShortcuts(string installDir, InstallOptions options)
    {
        var gui = GuiPath(installDir);

        if (options.DesktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Shortcuts.Create(Path.Combine(desktop, "RingControl.lnk"), gui, null, installDir,
                "RingControl · Ring monitoring and NVR");
        }
        else
        {
            DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RingControl.lnk"));
        }

        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RingControl");
        if (options.StartMenuShortcut)
        {
            Directory.CreateDirectory(startMenu);
            Shortcuts.Create(Path.Combine(startMenu, "RingControl.lnk"), gui, null, installDir,
                "RingControl · Ring monitoring and NVR");
            Shortcuts.Create(Path.Combine(startMenu, "RingControl (CLI).lnk"), CliPath(installDir), null,
                Path.Combine(installDir, "cli"), "RingControl console");

            // Open the recordings folder directly instead of launching the GUI.
            var recordings = DefaultRecordingsFolder();
            try
            {
                Directory.CreateDirectory(recordings);
            }
            catch
            {
                // The folder is created by the app on first run if this fails.
            }

            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Shortcuts.Create(Path.Combine(startMenu, "Open recordings.lnk"), explorer,
                $"\"{recordings}\"", recordings, "NVR recordings");
        }
        else
        {
            TryDeleteDirectory(startMenu, 0);
        }
    }

    private static void DeleteShortcuts(string installDir)
    {
        DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RingControl.lnk"));
        TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RingControl"), 0);
        DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "RingControl NVR.lnk"));
    }

    private static void ConfigureAutostart(string installDir, bool enabled)
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var link = Path.Combine(startup, "RingControl NVR.lnk");

        if (!enabled)
        {
            DeleteFile(link);
            return;
        }

        var cli = CliPath(installDir);
        if (!File.Exists(cli))
        {
            return;
        }

        Shortcuts.Create(link, cli, "nvr run --hidden", Path.Combine(installDir, "cli"),
            "RingControl NVR continuous recording");
    }

    private static void WriteRegistry(string installDir)
    {
        var gui = GuiPath(installDir);
        var setup = Path.Combine(installDir, "RingControlSetup.exe");

        using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
        {
            settings.SetValue("InstallDir", installDir);
            settings.SetValue("Version", Version);
            settings.SetValue("InstalledAt", DateTimeOffset.Now.ToString("O"));
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "RingControl");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", gui);
        key.SetValue("UninstallString", $"\"{setup}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{setup}\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(installDir), RegistryValueKind.DWord);
    }

    private static void DeleteRegistry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore.
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore.
        }
    }

    private static void PurgeConfiguration()
    {
        TryDeleteDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RingControl"), 2);

        TryDeleteDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RingControl"), 2);
    }

    private static void AddToPath(string directory)
    {
        var current = ReadUserPath();
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var updated = string.IsNullOrWhiteSpace(current)
            ? directory
            : current.TrimEnd(';') + ";" + directory;

        WriteUserPath(updated);
    }

    private static void RemoveFromPath(string directory)
    {
        var current = ReadUserPath();
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = parts
            .Where(p => !string.Equals(p.TrimEnd(Path.DirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == parts.Length)
        {
            return;
        }

        WriteUserPath(string.Join(';', filtered));
    }

    private static string ReadUserPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
            return key?.GetValue("Path") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void WriteUserPath(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey);
        key.SetValue("Path", value, RegistryValueKind.ExpandString);

        try
        {
            SendMessageTimeout((IntPtr)HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment",
                SmtoAbortIfHung, 3000, out _);
        }
        catch
        {
            // New terminals pick up the change anyway.
        }
    }

    private static void CloseRunningInstances()
    {
        foreach (var name in new[] { "RingControl", "ringcontrol" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: false);
                        process.WaitForExit(3000);
                    }
                }
                catch
                {
                    // A process may exit between enumeration and the kill.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static void ScheduleDirectoryRemoval(string directory)
    {
        try
        {
            var info = new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{directory}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(info);
        }
        catch
        {
            // The user can delete the folder manually.
        }
    }

    private static int EstimateSizeKb(string directory)
    {
        try
        {
            var bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0;
                    }
                });

            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore.
        }
    }

    private static void TryDeleteDirectory(string path, int retries)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                if (attempt == retries)
                {
                    return;
                }

                Thread.Sleep(400);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam,
        int flags, int timeout, out IntPtr result);
}
