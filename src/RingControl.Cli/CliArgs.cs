using System.Globalization;

namespace RingControl.Cli;

/// <summary>Error that should be shown to the user without a stack trace.</summary>
internal sealed class CliException : Exception
{
    public CliException(string message, int exitCode = 1) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

/// <summary>
/// Small, dependency-free argument reader. Supports <c>--name value</c>, <c>--name=value</c>
/// and boolean flags. Positionals are collected in order, ignoring options and their values.
/// </summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = new();

    public CliArgs(string[] args, params string[] booleanFlags)
    {
        var flags = new HashSet<string>(booleanFlags, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                _positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;

            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (!flags.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (value is null)
            {
                _flags.Add(name);
            }

            _options[name] = value;
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string? First => _positionals.Count > 0 ? _positionals[0] : null;

    public bool Has(string name) => _flags.Contains(name) || _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string? GetOrEnv(string name, string environmentVariable)
        => Get(name) ?? Environment.GetEnvironmentVariable(environmentVariable);

    public int? GetInt(string name)
    {
        var value = Get(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public long? GetLong(string name)
    {
        var value = Get(name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public double? GetDouble(string name)
    {
        var value = Get(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
