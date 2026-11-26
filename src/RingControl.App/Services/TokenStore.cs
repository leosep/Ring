using RingControl.Core.Platform;
using RingControl.Core.Security;

namespace RingControl.App.Services;

public sealed record StoredSession(string RefreshToken, string HardwareId, string? Email);

/// <summary>
/// Windows wrapper around the cross-platform <see cref="SessionStore"/>. The refresh token
/// is encrypted with DPAPI (current Windows user); the access token is never persisted.
/// </summary>
public sealed class TokenStore
{
    private readonly SessionStore _store = new();

    public static string FilePath => PlatformPaths.SessionFile;

    public StoredSession? Load()
    {
        var session = _store.Load();
        return session is null
            ? null
            : new StoredSession(session.RefreshToken, session.HardwareId, session.Email);
    }

    public void Save(string refreshToken, string hardwareId, string? email)
        => _store.Save(new StoredRingSession(refreshToken, hardwareId, email));

    public void Clear() => _store.Clear();
}
