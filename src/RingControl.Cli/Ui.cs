using System.Text;

namespace RingControl.Cli;

/// <summary>Console helpers that keep the CLI readable both in a terminal and in log files.</summary>
internal static class Ui
{
    private static readonly bool Color = !Console.IsOutputRedirected;

    public static void Header(string text)
    {
        Console.WriteLine();
        Write(ConsoleColor.Cyan, text);
        Write(ConsoleColor.DarkGray, new string('─', Math.Min(text.Length, 60)));
    }

    public static void Info(string text) => Write(ConsoleColor.Gray, "  " + text);

    public static void Success(string text) => Write(ConsoleColor.Green, "  ✓ " + text);

    public static void Warn(string text) => Write(ConsoleColor.Yellow, "  ! " + text);

    public static void Error(string text) => Write(ConsoleColor.Red, "  ✗ " + text);

    public static void Line(string text) => Console.WriteLine(text);

    public static void Blank() => Console.WriteLine();

    public static string Prompt(string label, string? defaultValue = null)
    {
        var suffix = string.IsNullOrWhiteSpace(defaultValue) ? string.Empty : $" [{defaultValue}]";
        Console.Write($"  {label}{suffix}: ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue ?? string.Empty : value.Trim();
    }

    /// <summary>Reads a secret without echoing it. Falls back to ReadLine when input is piped.</summary>
    public static string PromptSecret(string label)
    {
        Console.Write($"  {label}: ");

        if (Console.IsInputRedirected)
        {
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            builder.Append(key.KeyChar);
            Console.Write('*');
        }

        return builder.ToString();
    }

    public static bool Confirm(string label, bool defaultValue)
    {
        if (Console.IsInputRedirected)
        {
            return defaultValue;
        }

        var hint = defaultValue ? "S/n" : "s/N";
        Console.Write($"  {label} [{hint}]: ");
        var value = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.StartsWith("s", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    public static void Table(IReadOnlyList<string[]> rows, params string[] headers)
    {
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(row.Length, widths.Length); i++)
            {
                widths[i] = Math.Max(widths[i], DisplayWidth(row[i]));
            }
        }

        var headerLine = string.Join("  ", headers.Select((h, i) => Pad(h, widths[i])));
        Write(ConsoleColor.DarkGray, "  " + headerLine);

        foreach (var row in rows)
        {
            var line = string.Join("  ", row.Select((cell, i) => Pad(cell, i < widths.Length ? widths[i] : DisplayWidth(cell))));
            Console.WriteLine("  " + line);
        }
    }

    private static int DisplayWidth(string value)
    {
        var width = 0;
        foreach (var c in value)
        {
            width += c is >= '\u1100' and <= '\uD7A3' or >= '\uE000' and <= '\uF8FF' ? 2 : 1;
        }

        return width;
    }

    private static string Pad(string value, int width)
    {
        var padding = width - DisplayWidth(value);
        return padding > 0 ? value + new string(' ', padding) : value;
    }

    private static void Write(ConsoleColor color, string text)
    {
        if (!Color)
        {
            Console.WriteLine(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
