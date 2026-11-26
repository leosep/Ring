using System.Globalization;
using System.Text.Json.Nodes;

namespace RingControl.Core.Models;

public sealed record HealthItem(string Key, string Label, string Value);

/// <summary>Flattened view of Ring's device health payload.</summary>
public sealed class RingHealth
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["firmware_version"] = "Firmware",
        ["firmware"] = "Firmware",
        ["wifi_name"] = "Wi-Fi network",
        ["wifi_rssi"] = "Wi-Fi signal (RSSI)",
        ["wifi_rssi_category"] = "Wi-Fi quality",
        ["latest_signal_strength"] = "Wi-Fi signal (latest)",
        ["latest_signal_category"] = "Signal quality (latest)",
        ["average_signal_strength"] = "Wi-Fi signal (average)",
        ["average_signal_category"] = "Signal quality (average)",
        ["wifi_is_ring_network"] = "Ring network",
        ["packet_loss_strength"] = "Packet loss",
        ["packet_loss_category"] = "Packet quality",
        ["updated_at"] = "Last updated",
        ["battery_percentage"] = "Battery",
        ["battery_percentage_category"] = "Battery status",
        ["battery_voltage"] = "Battery voltage",
        ["battery_voltage_category"] = "Voltage status",
        ["external_connection"] = "External power",
        ["transformer_voltage"] = "Transformer voltage",
        ["transformer_voltage_category"] = "Transformer status",
        ["ext_power_state"] = "Power state",
        ["temperature"] = "Temperature",
        ["network_connection"] = "Network connection",
        ["connection_status"] = "Connection status",
        ["ip_address"] = "IP address",
        ["mac_address"] = "MAC address",
        ["signal_strength"] = "Signal strength",
        ["packet_loss"] = "Packet loss",
        ["ping"] = "Latency",
        ["uptime"] = "Uptime",
        ["last_update"] = "Last updated",
        ["device_id"] = "Device ID",
    };

    public List<HealthItem> Items { get; } = new();

    public string? Battery => Get("battery_percentage") ?? Get("battery_voltage");
    public string? WifiSignal => Get("wifi_rssi") ?? Get("signal_strength");
    public string? WifiName => Get("wifi_name");
    public string? Firmware => Get("firmware_version");
    public string? Temperature => Get("temperature");
    public string? IpAddress => Get("ip_address");

    public string? Get(string key) => Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    public static RingHealth FromJson(JsonNode? health)
    {
        var result = new RingHealth();
        if (health is not JsonObject obj)
        {
            return result;
        }

        foreach (var pair in obj)
        {
            if (pair.Value is null)
            {
                continue;
            }

            if (pair.Value is JsonObject nested)
            {
                foreach (var child in nested)
                {
                    if (child.Value is null)
                    {
                        continue;
                    }

                    result.Items.Add(MakeItem($"{pair.Key}.{child.Key}", Format(child.Value)));
                }
            }
            else
            {
                result.Items.Add(MakeItem(pair.Key, Format(pair.Value)));
            }
        }

        // Put the most useful values first, then keep the rest in a stable order.
        var preferred = new[]
        {
            "firmware_version", "battery_percentage", "battery_voltage", "wifi_name", "wifi_rssi",
            "wifi_rssi_category", "signal_strength", "network_connection", "connection_status",
            "external_connection", "transformer_voltage", "temperature", "ip_address", "mac_address",
        };

        result.Items.Sort((a, b) =>
        {
            var ai = Array.FindIndex(preferred, k => k.Equals(a.Key, StringComparison.OrdinalIgnoreCase));
            var bi = Array.FindIndex(preferred, k => k.Equals(b.Key, StringComparison.OrdinalIgnoreCase));
            if (ai < 0) ai = int.MaxValue;
            if (bi < 0) bi = int.MaxValue;
            return ai != bi ? ai.CompareTo(bi) : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    private static HealthItem MakeItem(string key, string value)
    {
        var label = Labels.TryGetValue(key, out var friendly) ? friendly : Prettify(key);
        return new HealthItem(key, label, TranslateValue(key, value));
    }

    private static string TranslateValue(string key, string value)
    {
        if (key.Equals("firmware", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("firmware_version", StringComparison.OrdinalIgnoreCase))
        {
            return value.Equals("Up to Date", StringComparison.OrdinalIgnoreCase) ? "Up to date" : value;
        }

        if (key.EndsWith("_category", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant() switch
            {
                "best" => "Excellent",
                "good" => "Good",
                "okay" => "Fair",
                "poor" => "Poor",
                "unknown" => "Unknown",
                _ => value,
            };
        }

        return value;
    }

    private static string Format(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag ? "Yes" : "No";
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number.ToString("0.##", CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return node.ToJsonString();
    }

    private static string Prettify(string key)
    {
        var text = key.Replace('_', ' ').Replace('.', ' ').Trim();
        return string.IsNullOrEmpty(text) ? key : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
