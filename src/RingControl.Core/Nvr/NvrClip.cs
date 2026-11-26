using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingControl.Core.Nvr;

/// <summary>Metadata stored next to every recording as a small JSON sidecar.</summary>
public sealed class NvrClip
{
    public string FilePath { get; set; } = string.Empty;

    public long DeviceId { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndedAt { get; set; }

    public double DurationSeconds { get; set; }

    public long Bytes { get; set; }

    public double FramesPerSecond { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public RecordingTrigger Trigger { get; set; }

    public string? EventId { get; set; }

    /// <summary>False when the file was still being written (or the process crashed).</summary>
    public bool Complete { get; set; } = true;

    [JsonIgnore]
    public string SidecarPath => Path.ChangeExtension(FilePath, ".json");

    [JsonIgnore]
    public string TriggerLabel => Trigger switch
    {
        RecordingTrigger.Ding => "Timbre",
        RecordingTrigger.Motion => "Movimiento",
        RecordingTrigger.OnDemand => "Vista en vivo",
        RecordingTrigger.Manual => "Manual",
        _ => "Continuo",
    };

    [JsonIgnore]
    public string TimeLabel => StartedAt.ToLocalTime().ToString("ddd d MMM · HH:mm:ss", new CultureInfo("es-ES"));

    [JsonIgnore]
    public string DurationLabel => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss")
        : string.Empty;

    [JsonIgnore]
    public string SizeLabel => NvrStorage.FormatBytes(Bytes);

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SidecarPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SidecarPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Metadata is best effort: the video file is the important part.
        }
    }

    public static NvrClip? Load(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath))
            {
                return null;
            }

            var clip = JsonSerializer.Deserialize<NvrClip>(File.ReadAllText(sidecarPath), Options);
            if (clip is null || string.IsNullOrWhiteSpace(clip.FilePath))
            {
                return null;
            }

            return clip;
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
