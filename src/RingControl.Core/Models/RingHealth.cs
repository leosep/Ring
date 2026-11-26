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
        ["wifi_name"] = "Red Wi-Fi",
        ["wifi_rssi"] = "Señal Wi-Fi (RSSI)",
        ["wifi_rssi_category"] = "Calidad Wi-Fi",
        ["latest_signal_strength"] = "Señal Wi-Fi (última)",
        ["latest_signal_category"] = "Calidad de señal (última)",
        ["average_signal_strength"] = "Señal Wi-Fi (media)",
        ["average_signal_category"] = "Calidad de señal (media)",
        ["wifi_is_ring_network"] = "Red de Ring",
        ["packet_loss_strength"] = "Pérdida de paquetes",
        ["packet_loss_category"] = "Calidad de paquetes",
        ["updated_at"] = "Última actualización",
        ["battery_percentage"] = "Batería",
        ["battery_percentage_category"] = "Estado de batería",
        ["battery_voltage"] = "Voltaje de batería",
        ["battery_voltage_category"] = "Estado del voltaje",
        ["external_connection"] = "Alimentación externa",
        ["transformer_voltage"] = "Voltaje del transformador",
        ["transformer_voltage_category"] = "Estado del transformador",
        ["ext_power_state"] = "Estado de alimentación",
        ["temperature"] = "Temperatura",
        ["network_connection"] = "Conexión de red",
        ["connection_status"] = "Estado de conexión",
        ["ip_address"] = "Dirección IP",
        ["mac_address"] = "Dirección MAC",
        ["signal_strength"] = "Intensidad de señal",
        ["packet_loss"] = "Pérdida de paquetes",
        ["ping"] = "Latencia",
        ["uptime"] = "Tiempo encendido",
        ["last_update"] = "Última actualización",
        ["device_id"] = "ID del dispositivo",
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
            return value.Equals("Up to Date", StringComparison.OrdinalIgnoreCase) ? "Actualizado" : value;
        }

        if (key.EndsWith("_category", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant() switch
            {
                "best" => "Excelente",
                "good" => "Buena",
                "okay" => "Correcta",
                "poor" => "Mala",
                "unknown" => "Desconocido",
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
                return flag ? "Sí" : "No";
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
