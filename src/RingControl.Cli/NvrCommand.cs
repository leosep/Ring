using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RingControl.Core.Models;
using RingControl.Core.Nvr;
using RingControl.Core.Platform;

namespace RingControl.Cli;

internal static class NvrCommand
{
    public static string StatusPath => Path.Combine(PlatformPaths.ConfigDirectory, "nvr-status.json");

    private static string PidPath => Path.Combine(PlatformPaths.ConfigDirectory, "nvr.pid");

    public static async Task<int> RunAsync(string[] rest)
    {
        var args = new CliArgs(rest, "json", "init", "help", "no-ding", "no-motion", "on-demand", "foreground", "once", "force");
        var sub = args.First?.ToLowerInvariant() ?? string.Empty;

        switch (sub)
        {
            case "run" or "start" or "daemon":
                return await RunDaemonAsync(args).ConfigureAwait(false);
            case "status":
                return Status(args);
            case "stop":
                return Stop();
            case "config" or "configuration":
                return Config(args);
            default:
                PrintHelp();
                return 2;
        }
    }

    private static void PrintHelp()
    {
        Ui.Header("NVR");
        Ui.Line("Usage: ringcontrol nvr <command> [options]");
        Console.WriteLine();
        Ui.Line("  run      Starts the NVR in the foreground (the service is the usual way)");
        Ui.Line("  status   Shows the running NVR state (--json)");
        Ui.Line("  stop     Stops the running NVR");
        Ui.Line("  config   Shows the configuration (--init creates it, --json)");
        Console.WriteLine();
        Ui.Line("Run options:");
        Ui.Line("  --config <file>      Alternative configuration");
        Ui.Line("  --root <folder>      Recordings folder");
        Ui.Line("  --mode events|continuous|both");
        Ui.Line("  --device <id,...>    Limits the NVR to those cameras");
        Ui.Line("  --segment <min>      Length of each continuous segment (1-60)");
        Ui.Line("  --clip <sec>         Length of event clips (10-600)");
        Ui.Line("  --retention <days>   Retention (0 = no day limit)");
        Ui.Line("  --max-gb <GB>        Disk quota (0 = no limit)");
        Ui.Line("  --max-streams <n>    Concurrent sessions (1-8)");
        Ui.Line("  --no-ding / --no-motion / --on-demand");
        Ui.Line("  --once               Records one test clip and exits");
        Console.WriteLine();
    }

    private static async Task<int> RunDaemonAsync(CliArgs args)
    {
        var configStore = new NvrConfigStore(args.Get("config"));
        var options = configStore.Load(createIfMissing: true);
        ApplyOverrides(args, options);
        options.Normalize();

        if (args.Has("once"))
        {
            return await RunOnceAsync(options, args).ConfigureAwait(false);
        }

        using var log = new DaemonLog(Path.Combine(PlatformPaths.LogsDirectory, "nvr.log"));
        log.Write($"RingControl NVR {Cli.Version} starting (pid {Environment.ProcessId}).");
        log.Write($"Configuration: {configStore.Path}");
        log.Write($"Mode {options.Mode} · folder {options.RootFolder} · segments {options.SegmentMinutes} min · " +
                  $"clips {options.EventClipSeconds} s · retention {options.RetentionDays} days · " +
                  $"quota {NvrStorage.FormatBytes(options.MaxStorageBytes)}");

        if (DaemonStatus.IsAlive(ReadPid()))
        {
            log.Write("Another NVR instance is already running. Exiting.");
            Ui.Error("Another NVR instance is already running.");
            return 1;
        }

        using var shutdown = new CancellationTokenSource();
        var registrations = RegisterSignals(shutdown);

        WritePid(Environment.ProcessId);
        var status = new DaemonStatus
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            Running = true,
            Mode = options.Mode.ToString(),
            RootFolder = options.RootFolder,
        };

        try
        {
            using var session = CliSession.Open();
            log.Write($"Ring session active for {session.Email ?? session.HardwareId}.");

            var devices = await session.Api.GetDevicesAsync(shutdown.Token).ConfigureAwait(false);
            log.Write($"Cameras in the account: {devices.Count}.");

            await using var engine = new NvrEngine(session.Api, options, log.Write);
            await engine.StartAsync(devices, shutdown.Token).ConfigureAwait(false);

            var syncTask = SyncLoopAsync(engine, session.Api, shutdown.Token, log);
            var statusTask = StatusLoopAsync(engine, status, shutdown.Token);

            try
            {
                await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }

            log.Write("Stopping the NVR…");
            TryCancel(shutdown);
            await WaitQuietlyAsync(syncTask).ConfigureAwait(false);
            await WaitQuietlyAsync(statusTask).ConfigureAwait(false);
            await engine.StopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.Write("NVR cancelled.");
        }
        catch (Exception ex)
        {
            log.Write("The NVR stopped because of an error: " + ex);
            status.Running = false;
            status.UpdatedAt = DateTimeOffset.Now;
            status.Save(StatusPath);
            DeletePid();
            return 1;
        }
        finally
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }

        status.Running = false;
        status.UpdatedAt = DateTimeOffset.Now;
        status.Save(StatusPath);
        DeletePid();
        log.Write("NVR stopped.");
        return 0;
    }

    private static async Task<int> RunOnceAsync(NvrOptions options, CliArgs args)
    {
        options.Mode = NvrMode.Events;

        using var log = new DaemonLog(Path.Combine(PlatformPaths.LogsDirectory, "nvr-test.log"));
        log.Write("NVR recording test (--once).");

        using var session = CliSession.Open();
        var devices = await session.Api.GetDevicesAsync().ConfigureAwait(false);
        var storage = new NvrStorage(options.RootFolder);

        var online = devices
            .Where(d => d.IsOnline && !d.LiveViewDisabled && options.Includes(d.Id))
            .Take(options.MaxConcurrentStreams)
            .ToList();

        if (online.Count == 0)
        {
            throw new CliException("There are no online cameras to test.");
        }

        Ui.Header("NVR test");
        Ui.Info($"Folder: {storage.Root}");

        var seconds = Math.Clamp(args.GetInt("seconds") ?? 10, 5, 60);
        var failures = 0;

        foreach (var device in online)
        {
            Ui.Info($"Recording {seconds} s from {device.Name}…");

            await using var recorder = new DeviceRecorder(session.Api, device, options, storage, log.Write);
            var ok = await recorder.RecordNowAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            var clip = storage.ListClips(device.Id).OrderByDescending(c => c.StartedAt).FirstOrDefault();

            if (!ok || clip is null)
            {
                Ui.Error($"{device.Name}: could not record.");
                failures++;
                continue;
            }

            Ui.Success($"{device.Name}: {clip.DurationLabel} · {clip.SizeLabel} · {clip.Width}x{clip.Height} · {clip.FramesPerSecond:0.##} fps");
        }

        Ui.Blank();
        if (failures == 0)
        {
            Ui.Success("Test completed. The NVR can record on this machine.");
            return 0;
        }

        Ui.Error($"{failures} camera(s) failed the test. Check the log: {log.LogPath}");
        return 1;
    }

    private static async Task SyncLoopAsync(NvrEngine engine, RingControl.Core.Api.RingApiClient api,
        CancellationToken ct, DaemonLog log)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var devices = await api.GetDevicesAsync(ct).ConfigureAwait(false);
                    await engine.SyncDevicesAsync(devices, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.Write("Could not refresh the camera list: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static async Task StatusLoopAsync(NvrEngine engine, DaemonStatus status, CancellationToken ct)
    {
        var usage = engine.GetUsage();
        var usageAt = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow - usageAt > TimeSpan.FromSeconds(60))
                    {
                        usage = engine.GetUsage();
                        usageAt = DateTimeOffset.UtcNow;
                    }

                    status.Running = true;
                    status.UpdatedAt = DateTimeOffset.Now;
                    status.StorageBytes = usage.Bytes;
                    status.ClipCount = usage.ClipCount;
                    status.FreeBytes = usage.FreeBytes;
                    status.Devices = engine.Recorders.Select(recorder =>
                    {
                        var current = recorder.GetStatus();
                        return new DaemonDeviceStatus
                        {
                            DeviceId = current.DeviceId,
                            DeviceName = current.DeviceName,
                            State = current.StateLabel,
                            Message = current.Message,
                            CurrentFile = current.CurrentFile,
                            CurrentSeconds = current.CurrentSeconds,
                            ClipsRecorded = current.ClipsRecorded,
                            LastClipAt = current.LastClipAt,
                        };
                    }).ToList();

                    status.Save(StatusPath);
                }
                catch
                {
                    // Status reporting must never stop the daemon.
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static int Status(CliArgs args)
    {
        var status = DaemonStatus.Load(StatusPath);
        var configStore = new NvrConfigStore(args.Get("config"));

        if (args.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                configFile = configStore.Path,
                status,
            }, DevicesCommand.JsonOptions));
            return 0;
        }

        Ui.Header("NVR");

        if (status is null)
        {
            Ui.Info("Not running (no saved state).");
            Ui.Info($"Configuration: {configStore.Path}");
            return 0;
        }

        if (status.ProcessAlive)
        {
            Ui.Success($"Running (pid {status.Pid}), {status.Mode} mode.");
        }
        else if (status.Running)
        {
            Ui.Warn($"Process {status.Pid} no longer exists. The NVR stopped unexpectedly.");
        }
        else
        {
            Ui.Info("Stopped.");
        }

        Ui.Info($"Started:     {status.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Ui.Info($"Updated:     {status.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Ui.Info($"Folder:      {status.RootFolder}");
        Ui.Info($"Recordings:  {status.ClipCount} clips · {NvrStorage.FormatBytes(status.StorageBytes)} · free {NvrStorage.FormatBytes(status.FreeBytes)}");

        if (status.Devices.Count > 0)
        {
            Ui.Blank();
            var rows = status.Devices.Select(d => new[]
            {
                d.DeviceName,
                d.State,
                d.CurrentSeconds > 0 ? $"{d.CurrentSeconds:0} s" : "—",
                d.ClipsRecorded.ToString(),
                d.LastClipAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—",
            }).ToList();

            Ui.Table(rows, "Camera", "State", "Current clip", "Clips", "Last");
        }

        return 0;
    }

    private static int Stop()
    {
        var status = DaemonStatus.Load(StatusPath);
        var pid = status?.Pid ?? ReadPid();

        if (!DaemonStatus.IsAlive(pid))
        {
            Ui.Warn("The NVR is not running.");
            DeletePid();
            return 0;
        }

        try
        {
            using var process = Process.GetProcessById(pid);

            if (!process.ProcessName.Contains("ringcontrol", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Process {pid} does not look like RingControl. It was not stopped for safety.");
            }

            if (OperatingSystem.IsWindows())
            {
                process.Kill(entireProcessTree: false);
            }
            else
            {
                using var kill = Process.Start("kill", new[] { "-TERM", pid.ToString() });
                kill?.WaitForExit(5000);
            }

            if (!process.WaitForExit(20_000))
            {
                Ui.Warn("The process did not exit in time; it was force-stopped.");
                process.Kill(entireProcessTree: true);
            }

            Ui.Success("NVR stopped.");
            DeletePid();
            return 0;
        }
        catch (CliException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CliException("Could not stop the NVR: " + ex.Message);
        }
    }

    private static int Config(CliArgs args)
    {
        var store = new NvrConfigStore(args.Get("config"));
        var existed = store.Exists;

        if (args.Has("init"))
        {
            var defaults = store.Load(createIfMissing: true);
            Ui.Success(existed
                ? $"The configuration already existed: {store.Path}"
                : $"Configuration created: {store.Path}");
            PrintOptions(defaults);
            return 0;
        }

        if (!existed)
        {
            Ui.Warn("No NVR configuration. Create it with: ringcontrol nvr config --init");
            Ui.Info($"Expected path: {store.Path}");
            return 1;
        }

        var options = store.Load(createIfMissing: false);

        if (args.Has("json"))
        {
            Console.WriteLine(File.ReadAllText(store.Path));
            return 0;
        }

        Ui.Header("NVR configuration");
        Ui.Info($"File: {store.Path}");
        PrintOptions(options);
        return 0;
    }

    private static void PrintOptions(NvrOptions options)
    {
        Ui.Blank();
        Ui.Info($"Mode:            {options.Mode}");
        Ui.Info($"Folder:          {options.RootFolder}");
        Ui.Info($"Segment:         {options.SegmentMinutes} min");
        Ui.Info($"Event clip:      {options.EventClipSeconds} s");
        Ui.Info($"Poll interval:   every {options.EventPollSeconds} s");
        Ui.Info($"Retention:       {(options.RetentionDays == 0 ? "no limit" : options.RetentionDays + " days")}");
        Ui.Info($"Quota:           {(options.MaxStorageBytes == 0 ? "no limit" : NvrStorage.FormatBytes(options.MaxStorageBytes))}");
        Ui.Info($"Sessions:        {options.MaxConcurrentStreams}");
        Ui.Info($"Doorbell:        {(options.RecordOnDing ? "record" : "do not record")}");
        Ui.Info($"Motion:          {(options.RecordOnMotion ? "record" : "do not record")}");
        Ui.Info($"Live view:       {(options.RecordOnDemand ? "record" : "do not record")}");
        Ui.Info($"Cameras:         {(options.DeviceIds.Count == 0 ? "all" : string.Join(", ", options.DeviceIds))}");
    }

    private static void ApplyOverrides(CliArgs args, NvrOptions options)
    {
        if (args.Get("root") is { Length: > 0 } root)
        {
            options.RootFolder = root;
        }

        if (args.Get("mode") is { Length: > 0 } mode)
        {
            options.Mode = ParseMode(mode);
        }

        if (args.Get("device") is { Length: > 0 } deviceIds)
        {
            var ids = new List<long>();
            foreach (var part in deviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(part, out var id))
                {
                    ids.Add(id);
                }
            }

            options.DeviceIds = ids;
        }

        if (args.GetInt("segment") is { } segment)
        {
            options.SegmentMinutes = segment;
        }

        if (args.GetInt("clip") is { } clip)
        {
            options.EventClipSeconds = clip;
        }

        if (args.GetInt("poll") is { } poll)
        {
            options.EventPollSeconds = poll;
        }

        if (args.GetInt("retention") is { } retention)
        {
            options.RetentionDays = retention;
        }

        if (args.GetDouble("max-gb") is { } maxGb)
        {
            options.MaxStorageBytes = (long)Math.Max(0, maxGb * 1024 * 1024 * 1024);
        }

        if (args.GetInt("max-streams") is { } streams)
        {
            options.MaxConcurrentStreams = streams;
        }

        if (args.Has("no-ding"))
        {
            options.RecordOnDing = false;
        }

        if (args.Has("no-motion"))
        {
            options.RecordOnMotion = false;
        }

        if (args.Has("on-demand"))
        {
            options.RecordOnDemand = true;
        }
    }

    private static NvrMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "continuous" or "continuo" or "24/7" => NvrMode.Continuous,
        "both" or "ambos" or "todo" => NvrMode.Both,
        "events" or "eventos" => NvrMode.Events,
        _ => throw new CliException($"Invalid mode: {value}. Use events, continuous or both."),
    };

    private static List<PosixSignalRegistration> RegisterSignals(CancellationTokenSource shutdown)
    {
        var registrations = new List<PosixSignalRegistration>();

        void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            TryCancel(shutdown);
        }

        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, Handle));
            }
            catch (PlatformNotSupportedException)
            {
                // Signal not available on this platform.
            }
            catch (ArgumentException)
            {
                // Signal not supported by the runtime.
            }
        }

        return registrations;
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // Already disposed or cancelling.
        }
    }

    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Background loops never surface errors.
        }
    }

    private static void WritePid(int pid)
    {
        try
        {
            Directory.CreateDirectory(PlatformPaths.ConfigDirectory);
            File.WriteAllText(PidPath, pid.ToString());
        }
        catch
        {
            // The pid file is only a convenience.
        }
    }

    private static int ReadPid()
    {
        try
        {
            return File.Exists(PidPath) && int.TryParse(File.ReadAllText(PidPath).Trim(), out var pid) ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void DeletePid()
    {
        try
        {
            if (File.Exists(PidPath))
            {
                File.Delete(PidPath);
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
