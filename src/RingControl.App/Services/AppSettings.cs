using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingControl.App.Services;

/// <summary>User preferences stored unencrypted in %APPDATA%\RingControl\settings.json.</summary>
public sealed class AppSettings
{
    public string HardwareId { get; set; } = Guid.NewGuid().ToString();

    public string? RememberedEmail { get; set; }

    public bool RememberEmail { get; set; } = true;

    public string RecordingFolder { get; set; } = DefaultRecordingFolder;

    public bool AutoStartLive { get; set; }

    /// <summary>Starts the in-app NVR as soon as the main window opens.</summary>
    public bool NvrAutoStart { get; set; }

    public int SnapshotIntervalSeconds { get; set; } = 30;

    public long? LastDeviceId { get; set; }

    [JsonIgnore]
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RingControl");

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    [JsonIgnore]
    public static string DefaultRecordingFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
            {
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }

            return Path.Combine(videos, "RingControl");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo leer la configuración", ex);
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo guardar la configuración", ex);
        }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(HardwareId))
        {
            HardwareId = Guid.NewGuid().ToString();
        }

        if (string.IsNullOrWhiteSpace(RecordingFolder))
        {
            RecordingFolder = DefaultRecordingFolder;
        }

        SnapshotIntervalSeconds = Math.Clamp(SnapshotIntervalSeconds, 10, 600);
    }
}
