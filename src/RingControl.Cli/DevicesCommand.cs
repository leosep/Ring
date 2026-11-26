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

        Ui.Header($"Cameras ({devices.Count})");
        PrintDevices(devices);

        var offline = devices.Count(d => !d.IsOnline);
        if (offline > 0)
        {
            Ui.Blank();
            Ui.Warn($"{offline} camera(s) offline.");
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

        Ui.Header("Account");
        Ui.Info($"Email:       {session.Email ?? "(not saved)"}");
        Ui.Info($"Device ID:   {session.HardwareId}");
        Ui.Info($"Session:     {PlatformPaths.SessionFile}");

        Ui.Header($"Cameras ({devices.Count})");
        PrintDevices(devices);

        Ui.Header("NVR");
        Ui.Info($"Config:      {new NvrConfigStore().Path}");

        if (status is null)
        {
            Ui.Info("State:       stopped (no saved state)");
        }
        else if (status.ProcessAlive)
        {
            Ui.Success($"Running (pid {status.Pid}), {status.Mode} mode.");
            Ui.Info($"Started:     {status.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            Ui.Info($"Recordings:  {status.ClipCount} clips · {NvrStorage.FormatBytes(status.StorageBytes)}");
            Ui.Info($"Folder:      {status.RootFolder}");

            foreach (var device in status.Devices)
            {
                var detail = device.State == "Recording" && !string.IsNullOrWhiteSpace(device.CurrentFile)
                    ? $" ({Path.GetFileName(device.CurrentFile)}, {device.CurrentSeconds:0} s)"
                    : string.Empty;

                Ui.Info($"  {device.DeviceName}: {device.State}{detail}");
            }
        }
        else
        {
            Ui.Warn($"Stopped. Last update: {status.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");
            Ui.Info("If it closed unexpectedly, check the service logs.");
        }

        return 0;
    }

    public static void PrintDevices(IReadOnlyList<RingDevice> devices)
    {
        if (devices.Count == 0)
        {
            Ui.Info("No devices.");
            return;
        }

        var rows = devices.Select(d => new[]
        {
            d.Id.ToString(),
            d.Name,
            d.ModelLabel,
            d.StatusLabel,
            d.BatteryLabel,
            d.MotionDetectionEnabled ? "Yes" : "No",
            d.ChimeEnabled ? "Yes" : "No",
            d.LiveViewDisabled ? "No" : "Yes",
        }).ToList();

        Ui.Table(rows, "ID", "Name", "Model", "Status", "Battery", "Motion", "Doorbell", "Live");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
