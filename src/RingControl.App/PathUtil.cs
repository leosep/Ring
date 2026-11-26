using System.IO;

namespace RingControl.App;

internal static class PathUtil
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static string SafeFileName(string? name, string fallback = "Ring")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var chars = name.Trim().Select(c => Invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
