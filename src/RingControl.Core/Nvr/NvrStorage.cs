using System.Globalization;
using System.Text;

namespace RingControl.Core.Nvr;

public sealed record NvrStorageUsage(long Bytes, int ClipCount, long FreeBytes, long TotalBytes)
{
    public string SizeLabel => NvrStorage.FormatBytes(Bytes);

    public string FreeLabel => NvrStorage.FormatBytes(FreeBytes);
}

/// <summary>
/// Folder layout and retention policy shared by the CLI daemon and the Windows GUI.
///
/// Layout: &lt;root&gt;/&lt;device-name&gt;_&lt;device-id&gt;/&lt;yyyy-MM-dd_HH-mm-ss&gt;_&lt;trigger&gt;.mp4
/// Every recording has a .json sidecar with the same name.
/// </summary>
public sealed class NvrStorage
{
    private readonly string _root;

    public NvrStorage(string root)
    {
        _root = string.IsNullOrWhiteSpace(root) ? NvrOptions.DefaultRootFolder : root;
    }

    public string Root => _root;

    public string DeviceFolder(long deviceId, string deviceName)
        => Path.Combine(_root, $"{SafeName(deviceName, "camara")}_{deviceId}");

    public string CreateClipPath(long deviceId, string deviceName, DateTimeOffset startedAt, RecordingTrigger trigger)
    {
        var folder = DeviceFolder(deviceId, deviceName);
        Directory.CreateDirectory(folder);

        var stamp = startedAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var name = $"{stamp}_{trigger.ToString().ToLowerInvariant()}";
        var path = Path.Combine(folder, name + ".mp4");

        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{name}_{i}.mp4");
        }

        return path;
    }

    public IReadOnlyList<NvrClip> ListClips(long? deviceId = null)
    {
        var clips = new List<NvrClip>();

        if (!Directory.Exists(_root))
        {
            return clips;
        }

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(_root);
        }
        catch
        {
            return clips;
        }

        foreach (var folder in folders)
        {
            if (deviceId is not null && !folder.EndsWith("_" + deviceId.Value, StringComparison.Ordinal))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.mp4");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var clip = NvrClip.Load(Path.ChangeExtension(file, ".json"));
                if (clip is null)
                {
                    clip = BuildFromFile(file, deviceId);
                }

                if (clip is not null && File.Exists(clip.FilePath))
                {
                    clips.Add(clip);
                }
            }
        }

        return clips
            .OrderByDescending(c => c.StartedAt)
            .ToList();
    }

    private static NvrClip? BuildFromFile(string file, long? deviceId)
    {
        try
        {
            var info = new FileInfo(file);
            var name = Path.GetFileNameWithoutExtension(file);
            var trigger = RecordingTrigger.Continuous;

            if (name.EndsWith("_ding", StringComparison.OrdinalIgnoreCase)) trigger = RecordingTrigger.Ding;
            else if (name.EndsWith("_motion", StringComparison.OrdinalIgnoreCase)) trigger = RecordingTrigger.Motion;
            else if (name.EndsWith("_ondemand", StringComparison.OrdinalIgnoreCase)) trigger = RecordingTrigger.OnDemand;
            else if (name.EndsWith("_manual", StringComparison.OrdinalIgnoreCase)) trigger = RecordingTrigger.Manual;

            var stampText = name.Length >= 19 ? name[..19] : string.Empty;
            var started = DateTimeOffset.TryParseExact(stampText, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);

            return new NvrClip
            {
                FilePath = file,
                DeviceId = deviceId ?? 0,
                DeviceName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "Cámara",
                StartedAt = started,
                EndedAt = started.AddSeconds(0),
                Bytes = info.Length,
                Trigger = trigger,
                Complete = true,
            };
        }
        catch
        {
            return null;
        }
    }

    public NvrStorageUsage GetUsage()
    {
        long bytes = 0;
        var count = 0;

        if (Directory.Exists(_root))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*.mp4", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        count++;
                    }
                    catch
                    {
                        // Ignore files that disappear while enumerating.
                    }
                }
            }
            catch
            {
                // Ignore unreadable folders.
            }
        }

        long free = 0;
        long total = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root)) ?? "/");
            if (drive.IsReady)
            {
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
        }
        catch
        {
            // DriveInfo is not available for every mount; the UI simply shows nothing.
        }

        return new NvrStorageUsage(bytes, count, free, total);
    }

    /// <summary>
    /// Deletes clips outside the retention window or above the storage quota, oldest first.
    /// Files in <paramref name="activeFiles"/> are never removed.
    /// </summary>
    public int EnforceRetention(NvrOptions options, ISet<string>? activeFiles = null)
    {
        var clips = ListClips().ToList();
        var removed = 0;
        var cutoff = options.RetentionDays > 0
            ? DateTimeOffset.Now.AddDays(-options.RetentionDays)
            : DateTimeOffset.MinValue;

        foreach (var clip in clips.ToList())
        {
            if (activeFiles is not null && activeFiles.Contains(clip.FilePath))
            {
                continue;
            }

            if (clip.StartedAt < cutoff)
            {
                if (Delete(clip))
                {
                    removed++;
                }

                clips.Remove(clip);
            }
        }

        if (options.MaxStorageBytes > 0)
        {
            long total = 0;
            foreach (var clip in clips)
            {
                try
                {
                    total += new FileInfo(clip.FilePath).Length;
                }
                catch
                {
                    // Ignore.
                }
            }

            foreach (var clip in clips.OrderBy(c => c.StartedAt))
            {
                if (total <= options.MaxStorageBytes)
                {
                    break;
                }

                if (activeFiles is not null && activeFiles.Contains(clip.FilePath))
                {
                    continue;
                }

                long size = 0;
                try
                {
                    size = new FileInfo(clip.FilePath).Length;
                }
                catch
                {
                    // Ignore.
                }

                if (Delete(clip))
                {
                    removed++;
                    total -= size;
                }
            }
        }

        return removed;
    }

    public bool Delete(NvrClip clip)
    {
        try
        {
            if (File.Exists(clip.FilePath))
            {
                File.Delete(clip.FilePath);
            }

            if (File.Exists(clip.SidecarPath))
            {
                File.Delete(clip.SidecarPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public static string SafeName(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : c);
        }

        var result = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
