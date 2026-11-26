using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RingControl.Core.Platform;

namespace RingControl.Core.Security;

public sealed record StoredRingSession(string RefreshToken, string HardwareId, string? Email);

/// <summary>
/// Stores the Ring refresh token (never the access token or the password).
///
/// Windows uses DPAPI scoped to the current user. Linux and macOS use AES-256-GCM with a
/// random key stored next to the session file and restricted to the owner (0600). The
/// legacy Windows format written by older builds is still readable.
/// </summary>
public sealed class SessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RingControl.Session.v1");
    private static readonly byte[] LegacyEntropy = Entropy;

    private readonly string _directory;

    public SessionStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? PlatformPaths.ConfigDirectory : directory;
    }

    public string DirectoryPath => _directory;

    public string FilePath => Path.Combine(_directory, "session.dat");

    private string KeyPath => Path.Combine(_directory, "session.key");

    public StoredRingSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var root = JsonNode.Parse(File.ReadAllText(FilePath));
            var payload = root?["payload"]?.GetValue<string>();
            var protection = root?["protection"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(payload))
            {
                var json = protection switch
                {
                    "dpapi" => UnprotectWindows(payload),
                    "aesgcm" => UnprotectAes(payload),
                    _ => null,
                };

                if (json is not null)
                {
                    return Deserialize(json);
                }
            }

            // Legacy format: {"Payload":"base64(dpapi)"} written by the first builds.
            var legacy = root?["Payload"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                var json = UnprotectWindows(legacy);
                return json is null ? null : Deserialize(json);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(StoredRingSession session)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(session);
        string protection;
        string payload;

        if (OperatingSystem.IsWindows())
        {
            protection = "dpapi";
            payload = Convert.ToBase64String(ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        }
        else
        {
            protection = "aesgcm";
            payload = ProtectAes(json);
        }

        System.IO.Directory.CreateDirectory(_directory);

        var wrapper = new JsonObject
        {
            ["version"] = 2,
            ["protection"] = protection,
            ["payload"] = payload,
        };

        WritePrivateFile(FilePath, wrapper.ToJsonString());
    }

    public void Clear()
    {
        TryDelete(FilePath);
        TryDelete(KeyPath);
    }

    private static StoredRingSession? Deserialize(byte[] json)
    {
        var session = JsonSerializer.Deserialize<StoredRingSession>(json);
        return string.IsNullOrWhiteSpace(session?.RefreshToken) ? null : session;
    }

    private string ProtectAes(byte[] plaintext)
    {
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var buffer = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(buffer, 0);
        tag.CopyTo(buffer, nonce.Length);
        cipher.CopyTo(buffer, nonce.Length + tag.Length);
        return Convert.ToBase64String(buffer);
    }

    private byte[]? UnprotectAes(string payload)
    {
        try
        {
            var buffer = Convert.FromBase64String(payload);
            if (buffer.Length < 28)
            {
                return null;
            }

            var key = GetOrCreateKey();
            var nonce = buffer.AsSpan(0, 12);
            var tag = buffer.AsSpan(12, 16);
            var cipher = buffer.AsSpan(28);
            var plaintext = new byte[cipher.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return plaintext;
        }
        catch
        {
            return null;
        }
    }

    private byte[] GetOrCreateKey()
    {
        try
        {
            if (File.Exists(KeyPath))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
                if (existing.Length == 32)
                {
                    return existing;
                }
            }
        }
        catch
        {
            // Regenerate a broken key file.
        }

        var key = RandomNumberGenerator.GetBytes(32);
        System.IO.Directory.CreateDirectory(_directory);
        WritePrivateFile(KeyPath, Convert.ToBase64String(key));
        return key;
    }

    private static byte[]? UnprotectWindows(string payload)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(payload), LegacyEntropy, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    private static void WritePrivateFile(string path, string contents)
    {
        File.WriteAllText(path, contents);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort on filesystems without Unix permissions.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
