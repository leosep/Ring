using System.IO;

namespace RingControl.Setup;

/// <summary>
/// Creates Windows shortcuts through the WScript.Shell COM object, which is present on
/// every Windows install. Runs on an STA thread because the shell object expects it.
/// </summary>
internal static class Shortcuts
{
    public static void Create(string linkPath, string targetPath, string? arguments = null,
        string? workingDirectory = null, string? description = null)
    {
        if (!File.Exists(targetPath))
        {
            return;
        }

        RunSta(() =>
        {
            var directory = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                            ?? throw new InvalidOperationException("WScript.Shell is not available on this system.");

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(linkPath);

            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments ?? string.Empty;
            shortcut.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(targetPath) ?? string.Empty;
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Description = description ?? "RingControl";
            shortcut.Save();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            throw new InvalidOperationException("Could not create the shortcut: " + error.Message, error);
        }
    }
}
