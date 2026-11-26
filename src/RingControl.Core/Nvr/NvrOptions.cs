using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingControl.Core.Nvr;

public enum NvrMode
{
    /// <summary>Only records while Ring reports a ding, motion or live-view event.</summary>
    Events,

    /// <summary>Records 24/7 in segments, reconnecting whenever Ring closes the stream.</summary>
    Continuous,

    /// <summary>Continuous recording plus a clip for every event.</summary>
    Both,
}

public enum RecordingTrigger
{
    Continuous,
    Ding,
    Motion,
    OnDemand,
    Manual,
}

/// <summary>
/// NVR configuration. Shared by the Linux daemon and the Windows GUI so both produce
/// the same folder layout and metadata.
/// </summary>
public sealed class NvrOptions
{
    public string RootFolder { get; set; } = DefaultRootFolder;

    public NvrMode Mode { get; set; } = NvrMode.Events;

    /// <summary>Length of each continuous segment, in minutes.</summary>
    public int SegmentMinutes { get; set; } = 5;

    /// <summary>How long an event clip is recorded, in seconds.</summary>
    public int EventClipSeconds { get; set; } = 45;

    /// <summary>Seconds between event polls when running in events mode.</summary>
    public int EventPollSeconds { get; set; } = 20;

    public int RetentionDays { get; set; } = 7;

    public long MaxStorageBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    public bool RecordOnDing { get; set; } = true;

    public bool RecordOnMotion { get; set; } = true;

    public bool RecordOnDemand { get; set; }

    /// <summary>Ring devices to include. Empty means every device on the account.</summary>
    public List<long> DeviceIds { get; set; } = new();

    /// <summary>Ring allows a limited number of simultaneous live views.</summary>
    public int MaxConcurrentStreams { get; set; } = 2;

    public bool RecordAudio { get; set; }

    [JsonIgnore]
    public static string DefaultRootFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
            {
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }

            return Path.Combine(videos, "RingControl");
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(RootFolder))
        {
            RootFolder = DefaultRootFolder;
        }

        SegmentMinutes = Math.Clamp(SegmentMinutes, 1, 60);
        EventClipSeconds = Math.Clamp(EventClipSeconds, 10, 600);
        EventPollSeconds = Math.Clamp(EventPollSeconds, 10, 600);
        RetentionDays = Math.Clamp(RetentionDays, 0, 3650);
        MaxConcurrentStreams = Math.Clamp(MaxConcurrentStreams, 1, 8);

        if (MaxStorageBytes < 0)
        {
            MaxStorageBytes = 0;
        }

        if (DeviceIds is null)
        {
            DeviceIds = new List<long>();
        }
    }

    public bool Includes(long deviceId) => DeviceIds.Count == 0 || DeviceIds.Contains(deviceId);

    public NvrOptions Clone() => new()
    {
        RootFolder = RootFolder,
        Mode = Mode,
        SegmentMinutes = SegmentMinutes,
        EventClipSeconds = EventClipSeconds,
        EventPollSeconds = EventPollSeconds,
        RetentionDays = RetentionDays,
        MaxStorageBytes = MaxStorageBytes,
        RecordOnDing = RecordOnDing,
        RecordOnMotion = RecordOnMotion,
        RecordOnDemand = RecordOnDemand,
        DeviceIds = new List<long>(DeviceIds),
        MaxConcurrentStreams = MaxConcurrentStreams,
        RecordAudio = RecordAudio,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static NvrOptions FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<NvrOptions>(json, JsonOptions) ?? new NvrOptions();
        options.Normalize();
        return options;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
