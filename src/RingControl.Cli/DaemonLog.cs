using System.Globalization;

namespace RingControl.Cli;

/// <summary>
/// Daemon logger: writes timestamped lines to stdout (captured by systemd/journald) and to
/// a rotating file. All IO is best effort so a full disk never kills the recorder.
/// </summary>
internal sealed class DaemonLog : IDisposable
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private const int MaxFiles = 3;

    private readonly object _sync = new();
    private readonly string _path;
    private StreamWriter? _writer;
    private long _written;

    public DaemonLog(string path)
    {
        _path = path;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            OpenWriter();
        }
        catch
        {
            _writer = null;
        }
    }

    public string LogPath => _path;

    public void Write(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (_sync)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // No console (detached process).
            }

            try
            {
                if (_writer is null)
                {
                    OpenWriter();
                }

                if (_writer is null)
                {
                    return;
                }

                _writer.WriteLine(line);
                _writer.Flush();
                _written += line.Length + Environment.NewLine.Length;

                if (_written >= MaxBytes)
                {
                    Rotate();
                }
            }
            catch
            {
                // Logging is not critical.
            }
        }
    }

    private void OpenWriter()
    {
        var info = new FileInfo(_path);
        _written = info.Exists ? info.Length : 0;

        _writer?.Dispose();
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            for (var i = MaxFiles - 1; i >= 1; i--)
            {
                var source = $"{_path}.{i}";
                var target = $"{_path}.{i + 1}";

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (File.Exists(source))
                {
                    File.Move(source, target);
                }
            }

            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".1");
            }
        }
        catch
        {
            // If rotation fails we keep appending to the current file.
        }

        _written = 0;
        OpenWriter();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // Ignore.
            }

            _writer = null;
        }
    }

    public static string Timestamp(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
