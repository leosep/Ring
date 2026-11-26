using System.Globalization;
using System.Text.Json.Nodes;

namespace RingControl.Core.Models;

internal static class JsonHelpers
{
    /// <summary>
    /// Safe child lookup: Ring returns some fields as plain strings in one model and as
    /// objects in another, so indexing a non-object node must not throw.
    /// </summary>
    private static JsonNode? Child(JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    public static string? Str(this JsonNode? node, string name)
    {
        var value = Child(node, name);
        if (value is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (jsonValue.TryGetValue<bool>(out var flag))
        {
            return flag ? "true" : "false";
        }

        if (jsonValue.TryGetValue<long>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        if (jsonValue.TryGetValue<double>(out var real))
        {
            return real.ToString(CultureInfo.InvariantCulture);
        }

        return jsonValue.ToJsonString().Trim('"');
    }

    public static long? Long(this JsonNode? node, string name)
    {
        var value = Child(node, name);
        if (value is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<long>(out var number))
        {
            return number;
        }

        if (jsonValue.TryGetValue<double>(out var real))
        {
            return (long)real;
        }

        return jsonValue.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static double? Double(this JsonNode? node, string name)
    {
        var value = Child(node, name);
        if (value is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<double>(out var real))
        {
            return real;
        }

        return jsonValue.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static bool? Bool(this JsonNode? node, string name)
    {
        var value = Child(node, name);
        if (value is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        if (jsonValue.TryGetValue<string>(out var text))
        {
            return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
        }

        if (jsonValue.TryGetValue<long>(out var number))
        {
            return number != 0;
        }

        return null;
    }

    public static JsonNode? Node(this JsonNode? node, string name) => Child(node, name);

    public static bool Has(this JsonNode? node, string name) => Child(node, name) is not null;

    public static DateTimeOffset? Date(this JsonNode? node, string name)
    {
        var value = Child(node, name);
        if (value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out var millis))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            }

            if (jsonValue.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}

public sealed class RingDevice
{
    private static readonly Dictionary<string, string> ModelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doorbell_graham_cracker"] = "Video Doorbell (1ª gen)",
        ["doorbell_graham_cracker_lite"] = "Video Doorbell (1ª gen)",
        ["doorbell_v3"] = "Video Doorbell 2",
        ["doorbell_v4"] = "Video Doorbell 3",
        ["doorbell_v5"] = "Video Doorbell 4",
        ["doorbell_v6"] = "Battery Doorbell Pro",
        ["doorbell_scallop"] = "Video Doorbell Pro",
        ["doorbell_scallop_lite"] = "Video Doorbell Wired",
        ["doorbell_scallop_sl"] = "Video Doorbell Pro 2",
        ["doorbell_pepper"] = "Video Doorbell Wired",
        ["doorbell_solo"] = "Video Doorbell",
        ["lpd_v1"] = "Video Doorbell Pro 2",
        ["lpd_v2"] = "Video Doorbell Pro 2",
        ["lpd_v4"] = "Video Doorbell Pro 2",
        ["stickup_cam"] = "Stick Up Cam",
        ["stickup_cam_v3"] = "Stick Up Cam (2ª gen)",
        ["stickup_cam_v4"] = "Stick Up Cam (3ª gen)",
        ["stickup_cam_lunar"] = "Stick Up Cam Pro",
        ["stickup_cam_mini"] = "Indoor Cam",
        ["stickup_cam_longfin"] = "Spotlight Cam Plus",
        ["stickup_cam_spotlight"] = "Spotlight Cam",
        ["stickup_cam_solar"] = "Solar Cam",
        ["spotlightw_v2"] = "Spotlight Cam Wired Pro",
        ["cocoa_camera"] = "Floodlight Cam",
        ["floodlight_v2"] = "Floodlight Cam Wired Pro",
        ["floodlight_pro"] = "Floodlight Cam Pro",
        ["onvif_camera"] = "Cámara ONVIF",
    };

    public long Id { get; set; }
    public string Name { get; set; } = "Dispositivo Ring";
    public string Kind { get; set; } = string.Empty;
    public string? DeviceType { get; set; }
    public string? LocationId { get; set; }
    public bool IsDoorbell { get; set; }
    public bool IsCamera { get; set; } = true;
    public bool IsOnline { get; set; } = true;
    public int? BatteryPercent { get; set; }
    public bool IsCharging { get; set; }
    public bool HasLight { get; set; }
    public bool HasSiren { get; set; }
    public bool MotionDetectionEnabled { get; set; } = true;
    public bool ChimeEnabled { get; set; }
    public bool LiveViewDisabled { get; set; }
    public string? Firmware { get; set; }
    public string? Address { get; set; }
    public string RawJson { get; set; } = "{}";

    public string ModelLabel => ModelNames.TryGetValue(Kind, out var label) ? label : PrettifyKind(Kind);

    public string KindLabel => IsDoorbell ? "Timbre" : "Cámara";

    public string StatusLabel => IsOnline ? "En línea" : "Sin conexión";

    public string BatteryLabel => BatteryPercent is null
        ? (IsCharging ? "Alimentación externa" : "—")
        : $"{BatteryPercent}%";

    public static RingDevice FromJson(JsonObject node, bool fromDoorbellArray)
    {
        var kind = node.Str("kind") ?? string.Empty;
        var battery1 = ReadBattery(node.Double("battery_life"));
        var battery2 = ReadBattery(node.Double("battery_life_2"));
        var battery = Min(battery1, battery2);
        var connection = node.Node("alerts")?.Str("connection");
        var settings = node.Node("settings");

        return new RingDevice
        {
            Id = node.Long("id") ?? 0,
            Name = node.Str("description") ?? node.Str("name") ?? "Dispositivo Ring",
            Kind = kind,
            DeviceType = node.Str("device_type"),
            LocationId = node.Str("location_id"),
            IsDoorbell = fromDoorbellArray || kind.StartsWith("doorbell", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("lpd_", StringComparison.OrdinalIgnoreCase),
            IsCamera = true,
            IsOnline = !string.Equals(connection, "offline", StringComparison.OrdinalIgnoreCase),
            BatteryPercent = battery,
            IsCharging = node.Bool("external_connection") == true,
            HasLight = node.Has("led_status"),
            HasSiren = node.Has("siren_status"),
            MotionDetectionEnabled = settings?.Bool("motion_detection_enabled") ?? true,
            ChimeEnabled = settings?.Node("chime_settings")?.Bool("enable") ?? false,
            LiveViewDisabled = settings?.Bool("live_view_disabled") ?? false,
            Firmware = node.Node("firmware_version")?.Str("version") ?? node.Str("firmware_version"),
            Address = node.Str("address"),
            RawJson = node.ToJsonString(),
        };
    }

    private static int? ReadBattery(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || value < 0 || value > 100)
        {
            return null;
        }

        return (int)Math.Round(value.Value);
    }

    private static int? Min(int? first, int? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return Math.Min(first.Value, second.Value);
    }

    private static string PrettifyKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "Dispositivo Ring";
        }

        var text = kind.Replace('_', ' ').Trim();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
