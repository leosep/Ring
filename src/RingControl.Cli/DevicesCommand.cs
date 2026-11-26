using System.Text.Json;
using RingControl.Core.Models;
using RingControl.Core.Nvr;
using RingControl.Core.Platform;

namespace RingControl.Cli;

internal static class DevicesCommand
{
    public static async Task<int> RunAsync(CliArgs args)
    {
        using var session = CliSession.Open();
        var devices = await session.Api.GetDevicesAsync().ConfigureAwait(false);

        if (args.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(devices.Select(d => new
            {
                d.Id,
                d.Name,
                d.Kind,
                d.ModelLabel,
                d.IsDoorbell,
                d.IsOnline,
                d.BatteryPercent,
                d.IsCharging,
                d.MotionDetectionEnabled,
                d.ChimeEnabled,
                d.LiveViewDisabled,
                d.HasLight,
                d.HasSiren,
                d.Firmware,
                d.LocationId,
            }), JsonOptions));
            return 0;
        }

        Ui.Header($"Cámaras ({devices.Count})");
        PrintDevices(devices);

        var offline = devices.Count(d => !d.IsOnline);
        if (offline > 0)
        {
            Ui.Blank();
            Ui.Warn($"{offline} cámara(s) sin conexión.");
        }

        return 0;
    }

    public static async Task<int> StatusAsync(CliArgs args)
    {
        using var session = CliSession.Open();
        var devices = await session.Api.GetDevicesAsync().ConfigureAwait(false);
        var status = DaemonStatus.Load(NvrCommand.StatusPath);
        var options = new NvrConfigStore().Load(createIfMissing: false);

        if (args.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                account = new { session.Email, session.HardwareId },
                configFile = new NvrConfigStore().Path,
                nvr = status,
                devices = devices.Select(d => new { d.Id, d.Name, d.IsOnline, d.MotionDetectionEnabled }),
            }, JsonOptions));
            return 0;
        }

        Ui.Header("Cuenta");
        Ui.Info($"Correo:      {session.Email ?? "(no guardado)"}");
        Ui.Info($"ID equipo:   {session.HardwareId}");
        Ui.Info($"Sesión:      {PlatformPaths.SessionFile}");

        Ui.Header($"Cámaras ({devices.Count})");
        PrintDevices(devices);

        Ui.Header("NVR");
        Ui.Info($"Configuración: {new NvrConfigStore().Path}");

        if (status is null)
        {
            Ui.Info("Estado:        detenido (no hay estado guardado)");
        }
        else if (status.ProcessAlive)
        {
            Ui.Success($"En ejecución (pid {status.Pid}), modo {status.Mode}.");
            Ui.Info($"Iniciado:      {status.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            Ui.Info($"Grabaciones:   {status.ClipCount} clips · {NvrStorage.FormatBytes(status.StorageBytes)}");
            Ui.Info($"Carpeta:       {status.RootFolder}");

            foreach (var device in status.Devices)
            {
                var detail = device.State == "Grabando" && !string.IsNullOrWhiteSpace(device.CurrentFile)
                    ? $" ({Path.GetFileName(device.CurrentFile)}, {device.CurrentSeconds:0} s)"
                    : string.Empty;

                Ui.Info($"  {device.DeviceName}: {device.State}{detail}");
            }
        }
        else
        {
            Ui.Warn($"Detenido. Última actualización: {status.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");
            Ui.Info("Si se cerró de forma inesperada, revisa los registros del servicio.");
        }

        return 0;
    }

    public static void PrintDevices(IReadOnlyList<RingDevice> devices)
    {
        if (devices.Count == 0)
        {
            Ui.Info("No hay dispositivos.");
            return;
        }

        var rows = devices.Select(d => new[]
        {
            d.Id.ToString(),
            d.Name,
            d.ModelLabel,
            d.StatusLabel,
            d.BatteryLabel,
            d.MotionDetectionEnabled ? "Sí" : "No",
            d.ChimeEnabled ? "Sí" : "No",
            d.LiveViewDisabled ? "No" : "Sí",
        }).ToList();

        Ui.Table(rows, "ID", "Nombre", "Modelo", "Estado", "Batería", "Movim.", "Timbre", "En vivo");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
