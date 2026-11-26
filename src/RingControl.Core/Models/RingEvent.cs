using System.Globalization;
using System.Text.Json.Nodes;

namespace RingControl.Core.Models;

/// <summary>A currently active ding/motion notification.</summary>
public sealed record RingDing(string Id, long DeviceId, string Kind, DateTimeOffset CreatedAt);

/// <summary>A single recorded event (motion, doorbell press, live view...) from a Ring camera.</summary>
public sealed class RingEvent
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "motion";
    public DateTimeOffset CreatedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? VideoUrl { get; set; }
    public string? UntranscodedUrl { get; set; }
    public bool PersonDetected { get; set; }
    public bool Recorded { get; set; }
    public bool HadSubscription { get; set; }
    public bool IsEndToEndEncrypted { get; set; }
    public string? State { get; set; }
    public string? RecordingStatus { get; set; }

    public bool IsDoorbellPress => Kind.Equals("ding", StringComparison.OrdinalIgnoreCase);

    public string KindLabel => Kind.ToLowerInvariant() switch
    {
        "ding" => "Timbre",
        "motion" => "Movimiento",
        "on_demand" => "Vista en vivo",
        "alarm" => "Alarma",
        "on_demand_link" => "Vista en vivo",
        "door_activity" => "Actividad en la puerta",
        "key_access" => "Acceso con llave",
        "offline_footage" => "Sin conexión",
        "offline_motion" => "Movimiento sin conexión",
        "deleted_footage" => "Grabación eliminada",
        _ => "Evento",
    };

    public string Glyph => Kind.ToLowerInvariant() switch
    {
        "ding" => "\uE7ED",       // Ringer
        "motion" => "\uE7F4",     // Motion / walking
        "on_demand" => "\uE714",  // Video
        "alarm" => "\uEA8F",      // Warning
        _ => "\uE7C3",            // Event
    };

    public string TimeLabel => CreatedAt.ToLocalTime().ToString("ddd d MMM · HH:mm", new CultureInfo("es-ES"));

    public string DurationLabel => DurationSeconds > 0 ? $"{DurationSeconds} s" : string.Empty;

    public string StateLabel => State switch
    {
        "timed_out" => "Sin respuesta",
        "completed" => "Completado",
        "ready" => "Disponible",
        "processing" => "Procesando",
        _ => string.Empty,
    };

    public string? PlaybackUrl => !string.IsNullOrWhiteSpace(VideoUrl) ? VideoUrl : UntranscodedUrl;

    public static RingEvent FromVideoSearch(JsonNode node)
    {
        var created = node.Date("created_at") ?? DateTimeOffset.UtcNow;
        return new RingEvent
        {
            Id = node.Str("ding_id") ?? node.Str("ding_id_str") ?? node.Str("id") ?? string.Empty,
            Kind = node.Str("kind") ?? "motion",
            CreatedAt = created,
            DurationSeconds = (int)(node.Double("duration") ?? 0),
            ThumbnailUrl = node.Str("thumbnail_url"),
            VideoUrl = node.Str("hq_url") ?? node.Str("lq_url"),
            UntranscodedUrl = node.Str("untranscoded_url"),
            PersonDetected = node.Node("cv_properties")?.Bool("person_detected") == true,
            State = node.Str("state"),
            RecordingStatus = node.Str("recording_status"),
        };
    }

    public static RingEvent FromLocationEvent(JsonNode node)
    {
        var created = node.Date("created_at") ?? DateTimeOffset.UtcNow;
        var kind = node.Str("kind") ?? node.Str("event_type") ?? "motion";
        var state = node.Str("state");
        var recordingStatus = node.Str("recording_status");
        var recorded = node.Bool("recorded") == true || string.Equals(recordingStatus, "ready", StringComparison.OrdinalIgnoreCase);

        return new RingEvent
        {
            Id = node.Str("ding_id_str") ?? node.Str("ding_id") ?? node.Str("event_id") ?? string.Empty,
            Kind = kind,
            CreatedAt = created,
            PersonDetected = node.Node("cv_properties")?.Bool("person_detected") == true,
            State = state,
            RecordingStatus = recordingStatus,
            Recorded = recorded,
            HadSubscription = node.Bool("had_subscription") == true,
            IsEndToEndEncrypted = node.Bool("is_e2ee") == true,
            ThumbnailUrl = node.Str("thumbnail_url"),
            VideoUrl = node.Str("hq_url") ?? node.Str("lq_url"),
            UntranscodedUrl = node.Str("untranscoded_url"),
        };
    }

    public static RingEvent FromEventApi(JsonNode node) => FromLocationEvent(node);

    public static RingEvent FromHistory(JsonNode node)
    {
        var created = node.Date("created_at") ?? DateTimeOffset.UtcNow;
        var answered = node.Bool("answered") == true;
        return new RingEvent
        {
            Id = node.Str("id") ?? node.Str("ding_id_str") ?? string.Empty,
            Kind = node.Str("kind") ?? "motion",
            CreatedAt = created,
            DurationSeconds = (int)(node.Double("duration") ?? 0),
            State = answered ? "completed" : "timed_out",
        };
    }
}
