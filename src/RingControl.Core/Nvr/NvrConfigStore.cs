using RingControl.Core.Platform;

namespace RingControl.Core.Nvr;

/// <summary>
/// Reads and writes <c>nvr.json</c>. The same file is used by the Linux daemon and the
/// Windows GUI, so a configuration edited from either side is picked up by the other.
/// </summary>
public sealed class NvrConfigStore
{
    private readonly string _path;

    public NvrConfigStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? PlatformPaths.DefaultNvrConfigFile : path;
    }

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    public NvrOptions Load(bool createIfMissing = true)
    {
        if (!File.Exists(_path))
        {
            var defaults = new NvrOptions();
            defaults.Normalize();

            if (createIfMissing)
            {
                Save(defaults);
            }

            return defaults;
        }

        try
        {
            var options = NvrOptions.FromJson(File.ReadAllText(_path));
            options.Normalize();
            return options;
        }
        catch (Exception)
        {
            // A corrupt or partially written file must not prevent the app from
            // starting. Fall back to defaults; they are persisted on the next save.
            var defaults = new NvrOptions();
            defaults.Normalize();
            return defaults;
        }
    }

    public void Save(NvrOptions options)
    {
        options.Normalize();

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, options.ToJson());
    }
}
