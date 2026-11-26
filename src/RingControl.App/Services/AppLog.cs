using System.IO;

namespace RingControl.App.Services;

/// <summary>Very small rolling log file used for diagnostics (no credentials are written).</summary>
public static class AppLog
{
    private static readonly object Sync = new();

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RingControl", "logs");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "ringcontrol.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    public static void Trim()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
