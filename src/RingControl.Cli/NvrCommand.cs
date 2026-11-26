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
        Ui.Line("Uso: ringcontrol nvr <comando> [opciones]");
        Console.WriteLine();
        Ui.Line("  run      Arranca el NVR en primer plano (lo normal es usar el servicio)");
        Ui.Line("  status   Muestra el estado del NVR en ejecución (--json)");
        Ui.Line("  stop     Detiene el NVR en ejecución");
        Ui.Line("  config   Muestra la configuración (--init la crea, --json)");
        Console.WriteLine();
        Ui.Line("Opciones de run:");
        Ui.Line("  --config <archivo>   Configuración alternativa");
        Ui.Line("  --root <carpeta>     Carpeta de grabaciones");
        Ui.Line("  --mode events|continuous|both");
        Ui.Line("  --device <id,...>    Limita el NVR a esas cámaras");
        Ui.Line("  --segment <min>      Duración de cada segmento continuo (1-60)");
        Ui.Line("  --clip <seg>         Duración de los clips por evento (10-600)");
        Ui.Line("  --retention <días>   Retención (0 = sin límite de días)");
        Ui.Line("  --max-gb <GB>        Cuota de disco (0 = sin límite)");
        Ui.Line("  --max-streams <n>    Sesiones simultáneas (1-8)");
        Ui.Line("  --no-ding / --no-motion / --on-demand");
        Ui.Line("  --once               Graba un clip de prueba y termina");
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
        log.Write($"RingControl NVR {Cli.Version} iniciando (pid {Environment.ProcessId}).");
        log.Write($"Configuración: {configStore.Path}");
        log.Write($"Modo {options.Mode} · carpeta {options.RootFolder} · segmentos {options.SegmentMinutes} min · " +
                  $"clips {options.EventClipSeconds} s · retención {options.RetentionDays} días · " +
                  $"cuota {NvrStorage.FormatBytes(options.MaxStorageBytes)}");

        if (DaemonStatus.IsAlive(ReadPid()))
        {
            log.Write("Ya hay otra instancia del NVR en ejecución. Saliendo.");
            Ui.Error("Ya hay otra instancia del NVR en ejecución.");
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
            log.Write($"Sesión de Ring activa para {session.Email ?? session.HardwareId}.");

            var devices = await session.Api.GetDevicesAsync(shutdown.Token).ConfigureAwait(false);
            log.Write($"Cámaras en la cuenta: {devices.Count}.");

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

            log.Write("Deteniendo el NVR…");
            TryCancel(shutdown);
            await WaitQuietlyAsync(syncTask).ConfigureAwait(false);
            await WaitQuietlyAsync(statusTask).ConfigureAwait(false);
            await engine.StopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.Write("NVR cancelado.");
        }
        catch (Exception ex)
        {
            log.Write("El NVR se detuvo por un error: " + ex);
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
        log.Write("NVR detenido.");
        return 0;
    }

    private static async Task<int> RunOnceAsync(NvrOptions options, CliArgs args)
    {
        options.Mode = NvrMode.Events;

        using var log = new DaemonLog(Path.Combine(PlatformPaths.LogsDirectory, "nvr-test.log"));
        log.Write("Prueba de grabación del NVR (--once).");

        using var session = CliSession.Open();
        var devices = await session.Api.GetDevicesAsync().ConfigureAwait(false);
        var storage = new NvrStorage(options.RootFolder);

        var online = devices
            .Where(d => d.IsOnline && !d.LiveViewDisabled && options.Includes(d.Id))
            .Take(options.MaxConcurrentStreams)
            .ToList();

        if (online.Count == 0)
        {
            throw new CliException("No hay cámaras en línea para probar.");
        }

        Ui.Header("Prueba del NVR");
        Ui.Info($"Carpeta: {storage.Root}");

        var seconds = Math.Clamp(args.GetInt("seconds") ?? 10, 5, 60);
        var failures = 0;

        foreach (var device in online)
        {
            Ui.Info($"Grabando {seconds} s de {device.Name}…");

            await using var recorder = new DeviceRecorder(session.Api, device, options, storage, log.Write);
            var ok = await recorder.RecordNowAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            var clip = storage.ListClips(device.Id).OrderByDescending(c => c.StartedAt).FirstOrDefault();

            if (!ok || clip is null)
            {
                Ui.Error($"{device.Name}: no se pudo grabar.");
                failures++;
                continue;
            }

            Ui.Success($"{device.Name}: {clip.DurationLabel} · {clip.SizeLabel} · {clip.Width}x{clip.Height} · {clip.FramesPerSecond:0.##} fps");
        }

        Ui.Blank();
        if (failures == 0)
        {
            Ui.Success("Prueba completada. El NVR puede grabar en esta máquina.");
            return 0;
        }

        Ui.Error($"{failures} cámara(s) fallaron la prueba. Revisa el registro: {log.LogPath}");
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
                    log.Write("No se pudo actualizar la lista de cámaras: " + ex.Message);
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
            Ui.Info("No se está ejecutando (no hay estado guardado).");
            Ui.Info($"Configuración: {configStore.Path}");
            return 0;
        }

        if (status.ProcessAlive)
        {
            Ui.Success($"En ejecución (pid {status.Pid}), modo {status.Mode}.");
        }
        else if (status.Running)
        {
            Ui.Warn($"El proceso {status.Pid} ya no existe. El NVR se detuvo de forma inesperada.");
        }
        else
        {
            Ui.Info("Detenido.");
        }

        Ui.Info($"Iniciado:     {status.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Ui.Info($"Actualizado:  {status.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Ui.Info($"Carpeta:      {status.RootFolder}");
        Ui.Info($"Grabaciones:  {status.ClipCount} clips · {NvrStorage.FormatBytes(status.StorageBytes)} · libre {NvrStorage.FormatBytes(status.FreeBytes)}");

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

            Ui.Table(rows, "Cámara", "Estado", "Clip actual", "Clips", "Último");
        }

        return 0;
    }

    private static int Stop()
    {
        var status = DaemonStatus.Load(StatusPath);
        var pid = status?.Pid ?? ReadPid();

        if (!DaemonStatus.IsAlive(pid))
        {
            Ui.Warn("El NVR no está en ejecución.");
            DeletePid();
            return 0;
        }

        try
        {
            using var process = Process.GetProcessById(pid);

            if (!process.ProcessName.Contains("ringcontrol", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"El proceso {pid} no parece ser RingControl. No se detuvo por seguridad.");
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
                Ui.Warn("El proceso no terminó a tiempo; se forzó la detención.");
                process.Kill(entireProcessTree: true);
            }

            Ui.Success("NVR detenido.");
            DeletePid();
            return 0;
        }
        catch (CliException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CliException("No se pudo detener el NVR: " + ex.Message);
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
                ? $"La configuración ya existía: {store.Path}"
                : $"Configuración creada: {store.Path}");
            PrintOptions(defaults);
            return 0;
        }

        if (!existed)
        {
            Ui.Warn("No hay configuración del NVR. Créala con: ringcontrol nvr config --init");
            Ui.Info($"Ruta prevista: {store.Path}");
            return 1;
        }

        var options = store.Load(createIfMissing: false);

        if (args.Has("json"))
        {
            Console.WriteLine(File.ReadAllText(store.Path));
            return 0;
        }

        Ui.Header("Configuración del NVR");
        Ui.Info($"Archivo: {store.Path}");
        PrintOptions(options);
        return 0;
    }

    private static void PrintOptions(NvrOptions options)
    {
        Ui.Blank();
        Ui.Info($"Modo:            {options.Mode}");
        Ui.Info($"Carpeta:         {options.RootFolder}");
        Ui.Info($"Segmento:        {options.SegmentMinutes} min");
        Ui.Info($"Clip por evento: {options.EventClipSeconds} s");
        Ui.Info($"Consulta:        cada {options.EventPollSeconds} s");
        Ui.Info($"Retención:       {(options.RetentionDays == 0 ? "sin límite" : options.RetentionDays + " días")}");
        Ui.Info($"Cuota:           {(options.MaxStorageBytes == 0 ? "sin límite" : NvrStorage.FormatBytes(options.MaxStorageBytes))}");
        Ui.Info($"Sesiones:        {options.MaxConcurrentStreams}");
        Ui.Info($"Timbre:          {(options.RecordOnDing ? "grabar" : "no grabar")}");
        Ui.Info($"Movimiento:      {(options.RecordOnMotion ? "grabar" : "no grabar")}");
        Ui.Info($"Vista en vivo:   {(options.RecordOnDemand ? "grabar" : "no grabar")}");
        Ui.Info($"Cámaras:         {(options.DeviceIds.Count == 0 ? "todas" : string.Join(", ", options.DeviceIds))}");
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
        _ => throw new CliException($"Modo no válido: {value}. Usa events, continuous o both."),
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
