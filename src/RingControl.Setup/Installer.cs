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

    public static async Task InstallAsync(InstallOptions options, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("El instalador solo funciona en Windows.");
        }

        var installDir = NormalizeInstallDir(options.InstallDir);

        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                            ?? throw new InvalidOperationException(
                                "Este instalador se compiló sin contenido. Descarga el instalador completo.");

        progress.Report(new SetupProgress("Comprobando la instalación…", 2));
        await Task.Run(CloseRunningInstances, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var staging = installDir.TrimEnd(Path.DirectorySeparatorChar) + ".staging";

        try
        {
            TryDeleteDirectory(staging, 0);
            Directory.CreateDirectory(staging);

            ExtractPayload(payload, staging, progress, ct);

            progress.Report(new SetupProgress("Instalando archivos…", 70));
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

            Directory.Move(staging, installDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDeleteDirectory(staging, 0);
            throw new InvalidOperationException(
                $"No se puede escribir en \"{installDir}\". Elige una carpeta dentro de tu perfil de usuario.", ex);
        }
        catch
        {
            TryDeleteDirectory(staging, 0);
            throw;
        }

        ct.ThrowIfCancellationRequested();

        progress.Report(new SetupProgress("Preparando el desinstalador…", 76));
        CopySelfInto(installDir);

        progress.Report(new SetupProgress("Creando accesos directos…", 82));
        CreateShortcuts(installDir, options);

        progress.Report(new SetupProgress("Configurando el inicio automático…", 88));
        ConfigureAutostart(installDir, options.AutostartNvr);

        progress.Report(new SetupProgress("Actualizando el PATH…", 92));
        if (options.AddToPath)
        {
            AddToPath(Path.Combine(installDir, "cli"));
        }
        else
        {
            RemoveFromPath(Path.Combine(installDir, "cli"));
        }

        progress.Report(new SetupProgress("Registrando la aplicación…", 96));
        WriteRegistry(installDir);

        progress.Report(new SetupProgress("Instalación completada.", 100));
    }

    public static async Task UninstallAsync(InstallOptions options, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("El desinstalador solo funciona en Windows.");
        }

        var installDir = !string.IsNullOrWhiteSpace(options.InstallDir)
            ? NormalizeInstallDir(options.InstallDir)
            : InstalledDir ?? InstallOptions.DefaultDir;

        progress.Report(new SetupProgress("Cerrando aplicaciones…", 5));
        await Task.Run(CloseRunningInstances, ct).ConfigureAwait(false);

        progress.Report(new SetupProgress("Eliminando accesos directos…", 20));
        DeleteShortcuts(installDir);
        ConfigureAutostart(installDir, enabled: false);

        progress.Report(new SetupProgress("Actualizando el PATH…", 35));
        RemoveFromPath(Path.Combine(installDir, "cli"));

        progress.Report(new SetupProgress("Eliminando el registro…", 50));
        DeleteRegistry();

        if (options.PurgeData)
        {
            progress.Report(new SetupProgress("Eliminando configuración y sesión…", 60));
            PurgeConfiguration();
        }

        progress.Report(new SetupProgress("Eliminando archivos…", 70));
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

        progress.Report(new SetupProgress("Desinstalación completada.", 100));
    }

    public static void Launch(string installDir)
    {
        var exe = GuiPath(installDir);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("No se encontró RingControl.exe.", exe);
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
            throw new InvalidOperationException("Indica una carpeta de instalación.");
        }

        var full = Path.GetFullPath(value.Trim().Trim('"'));
        var root = Path.GetPathRoot(full);

        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No se puede instalar en la raíz de una unidad.");
        }

        if (full.Length < 12)
        {
            throw new InvalidOperationException("La ruta de instalación es demasiado corta. Elige una carpeta como C:\\Users\\TuUsuario\\Programs\\RingControl.");
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
                throw new InvalidDataException("El paquete de instalación contiene una ruta no válida.");
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
                progress.Report(new SetupProgress($"Instalando archivos… ({done}/{entries.Count})", percent));
            }
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
                "RingControl · monitorización y NVR de Ring");
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
                "RingControl · monitorización y NVR de Ring");
            Shortcuts.Create(Path.Combine(startMenu, "RingControl (CLI).lnk"), CliPath(installDir), null,
                Path.Combine(installDir, "cli"), "Consola de RingControl");
            Shortcuts.Create(Path.Combine(startMenu, "Abrir grabaciones.lnk"),
                Path.Combine(installDir, "RingControl.exe"), null, installDir, "Grabaciones del NVR");
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
            "Grabación continua del NVR de RingControl");
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
