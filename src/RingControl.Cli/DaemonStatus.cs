using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingControl.Cli;

/// <summary>Status snapshot written by the daemon so <c>nvr status</c> needs no Ring login.</summary>
internal sealed class DaemonStatus
{
    public int Pid { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool Running { get; set; }

    public string Mode { get; set; } = string.Empty;

    public string RootFolder { get; set; } = string.Empty;

    public long StorageBytes { get; set; }

    public int ClipCount { get; set; }

    public long FreeBytes { get; set; }

    public List<DaemonDeviceStatus> Devices { get; set; } = new();

    [JsonIgnore]
    public bool ProcessAlive => Running && IsAlive(Pid);

    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Save(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Status reporting must never stop the daemon.
        }
    }

    public static DaemonStatus? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DaemonStatus>(File.ReadAllText(path), Options)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed class DaemonDeviceStatus
{
    public long DeviceId { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? CurrentFile { get; set; }

    public double CurrentSeconds { get; set; }

    public int ClipsRecorded { get; set; }

    public DateTimeOffset? LastClipAt { get; set; }
}
