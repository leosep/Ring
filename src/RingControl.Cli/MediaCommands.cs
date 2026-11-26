using System.Text.Json;
using RingControl.Core.Api;
using RingControl.Core.Nvr;
using RingControl.Core.Platform;

namespace RingControl.Cli;

internal static class MediaCommands
{
    public static async Task<int> SnapshotAsync(CliArgs args)
    {
        using var session = CliSession.Open();
        var device = session.ResolveDevice(args.First);

        Ui.Header($"Snapshot · {device.Name}");
        Ui.Info("Requesting an image from the camera…");

        byte[]? bytes;
        try
        {
            bytes = await session.Api.GetSnapshotAsync(device.Id, force: true, CancellationToken.None, timeoutSeconds: 25)
                .ConfigureAwait(false);
        }
        catch (RingApiException ex)
        {
            throw new CliException(ex.Message);
        }

        if (bytes is null || bytes.Length == 0)
        {
            throw new CliException("Ring did not return a snapshot. Try again in a few seconds.");
        }

        var path = args.Get("out");
        if (string.IsNullOrWhiteSpace(path))
        {
            var folder = Path.Combine(PlatformPaths.DataDirectory, "snapshots");
            path = Path.Combine(folder,
                $"{NvrStorage.SafeName(device.Name, "camera")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jpg");
        }

        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        Ui.Success($"Saved: {path} ({NvrStorage.FormatBytes(bytes.Length)})");
        return 0;
    }

    public static async Task<int> RecordAsync(CliArgs args)
    {
        var seconds = Math.Clamp(args.GetInt("seconds") ?? args.GetInt("duration") ?? 30, 5, 3600);
        var config = new NvrConfigStore(args.Get("config"));
        var options = config.Load(createIfMissing: false);
        var root = args.Get("root") ?? options.RootFolder;

        options.RootFolder = root;
        options.Mode = NvrMode.Events;
        options.EventClipSeconds = seconds;
        options.Normalize();

        var storage = new NvrStorage(root);

        using var session = CliSession.Open();
        var device = session.ResolveDevice(args.First);

        Ui.Header($"Manual recording · {device.Name}");
        Ui.Info($"Duration: {seconds} s · Folder: {storage.Root}");

        var interactive = !Console.IsOutputRedirected;
        var lastDraw = DateTimeOffset.MinValue;

        await using var recorder = new DeviceRecorder(session.Api, device, options, storage,
            message => Ui.Info(message));

        recorder.StatusChanged += (_, status) =>
        {
            if (!interactive)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - lastDraw < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            lastDraw = now;
            var line = $"  {status.StateLabel,-12} {status.CurrentSeconds,6:0.0} s / {seconds} s · {NvrStorage.FormatBytes(status.CurrentBytes)}";
            Console.Write("\r" + line.PadRight(Math.Min(Console.WindowWidth - 1, 90)));
        };

        var ok = await recorder.RecordNowAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

        if (interactive)
        {
            Console.WriteLine();
        }

        if (!ok)
        {
            throw new CliException("Could not start recording (is another one already running?).");
        }

        var clip = storage.ListClips(device.Id).OrderByDescending(c => c.StartedAt).FirstOrDefault();
        if (clip is null)
        {
            throw new CliException("The recording did not produce any file. Check the camera connection.");
        }

        Ui.Success($"Saved: {clip.FilePath}");
        Ui.Info($"{clip.DurationLabel} · {clip.SizeLabel} · {clip.Width}x{clip.Height} · {clip.FramesPerSecond:0.##} fps");
        return 0;
    }

    public static Task<int> ClipsAsync(CliArgs args)
    {
        var config = new NvrConfigStore(args.Get("config"));
        var options = config.Load(createIfMissing: false);
        var root = args.Get("root") ?? options.RootFolder;
        options.RootFolder = root;

        var storage = new NvrStorage(root);
        long? deviceId = args.GetLong("device");

        if (args.Has("prune"))
        {
            var removed = storage.EnforceRetention(options);
            Ui.Success($"Retention applied: {removed} recording(s) deleted.");
        }

        var clips = storage.ListClips(deviceId);
        var usage = storage.GetUsage();

        if (args.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                root = storage.Root,
                totalBytes = usage.Bytes,
                totalClips = usage.ClipCount,
                clips,
            }, DevicesCommand.JsonOptions));
            return Task.FromResult(0);
        }

        Ui.Header($"Recordings ({clips.Count})");
        Ui.Info($"Folder: {storage.Root}");
        Ui.Info($"Total: {usage.ClipCount} clips · {usage.SizeLabel} · free {usage.FreeLabel}");
        Ui.Blank();

        if (clips.Count == 0)
        {
            Ui.Info("No recordings yet.");
            return Task.FromResult(0);
        }

        var rows = clips
            .Take(200)
            .Select(c => new[]
            {
                c.TimeLabel,
                c.DeviceName,
                c.TriggerLabel,
                c.DurationLabel,
                c.SizeLabel,
                Path.GetFileName(c.FilePath),
            })
            .ToList();

        Ui.Table(rows, "Date", "Camera", "Type", "Duration", "Size", "File");

        if (clips.Count > 200)
        {
            Ui.Blank();
            Ui.Info($"Showing the 200 most recent of {clips.Count}. Use --json for the full list.");
        }

        return Task.FromResult(0);
    }
}
