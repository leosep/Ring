using System.Net.Http;
using RingControl.Core.Api;
using RingControl.Core.Auth;

namespace RingControl.App.Services;

/// <summary>Owns the Ring credentials for the current run and builds the API client.</summary>
public sealed class RingSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly RingAuthClient _auth;
    private RingApiClient? _api;

    public RingSession(HttpClient http, string hardwareId)
    {
        _http = http;
        HardwareId = hardwareId;
        _auth = new RingAuthClient(http, hardwareId);
    }

    public string HardwareId { get; }

    public RingApiClient? Api => _api;

    public RingTokens? Tokens { get; private set; }

    /// <summary>Raised when Ring rotates the refresh token, so it can be persisted again.</summary>
    public event EventHandler<RingTokens>? TokensUpdated;

    public RingApiClient RequireApi() => _api ?? throw new InvalidOperationException("No hay una sesión de Ring activa.");

    public async Task<RingTokens> LoginAsync(string email, string password, string? twoFactorCode, CancellationToken ct = default)
    {
        var tokens = await _auth.LoginAsync(email, password, twoFactorCode, ct).ConfigureAwait(false);
        Attach(tokens);
        return tokens;
    }

    /// <summary>Restores a previous session from a stored refresh token. Never throws.</summary>
    public async Task<bool> TryRestoreAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            var tokens = await _auth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            Attach(tokens);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("No se pudo restaurar la sesión guardada: " + ex.Message);
            return false;
        }
    }

    private void Attach(RingTokens tokens)
    {
        _api?.Dispose();

        Tokens = tokens;
        _api = new RingApiClient(_http, _auth, HardwareId, tokens);
        _api.TokensRefreshed += (_, refreshed) =>
        {
            Tokens = refreshed;
            TokensUpdated?.Invoke(this, refreshed);
        };
    }

    public void Dispose()
    {
        _api?.Dispose();
        _api = null;
    }
}
