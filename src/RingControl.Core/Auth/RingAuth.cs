using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace RingControl.Core.Auth;

/// <summary>Tokens returned by Ring's OAuth endpoint.</summary>
public sealed record RingTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Raised when Ring's OAuth endpoint rejects a login or a refresh. When
/// <see cref="RequiresTwoFactor"/> is true the caller should ask the user for the
/// verification code and call <see cref="RingAuthClient.LoginAsync"/> again.
/// </summary>
public sealed class RingAuthException : Exception
{
    public bool RequiresTwoFactor { get; init; }
    public string? Prompt { get; init; }
    public int? StatusCode { get; init; }

    public RingAuthException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Minimal implementation of Ring's unofficial OAuth flow (password grant + 2FA + refresh token).
/// The flow mirrors what the official Android app and the community libraries use.
/// </summary>
public sealed class RingAuthClient
{
    private const string TokenUrl = "https://oauth.ring.com/oauth/token";
    private readonly HttpClient _http;
    private readonly string _hardwareId;

    public RingAuthClient(HttpClient http, string hardwareId)
    {
        _http = http;
        _hardwareId = hardwareId;
    }

    public Task<RingTokens> LoginAsync(string email, string password, string? twoFactorCode, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["client_id"] = "ring_official_android",
            ["scope"] = "client",
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = password,
        };

        return RequestTokenAsync(body, twoFactorCode, ct);
    }

    public Task<RingTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["client_id"] = "ring_official_android",
            ["scope"] = "client",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };

        return RequestTokenAsync(body, null, ct);
    }

    private async Task<RingTokens> RequestTokenAsync(JsonObject body, string? twoFactorCode, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.TryAddWithoutValidation("2fa-support", "true");
        request.Headers.TryAddWithoutValidation("2fa-code", twoFactorCode ?? string.Empty);
        request.Headers.TryAddWithoutValidation("hardware_id", _hardwareId);
        request.Headers.TryAddWithoutValidation("User-Agent", "android:com.ringapp");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RingAuthException("No se pudo conectar con Ring. Comprueba tu conexión a Internet.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? node = null;
            try
            {
                node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch
            {
                // Ring occasionally returns HTML error pages from its edge.
            }

            if (!response.IsSuccessStatusCode)
            {
                throw BuildAuthError(response.StatusCode, node, text, isRefresh: body["grant_type"]?.GetValue<string>() == "refresh_token");
            }

            var accessToken = node?["access_token"]?.GetValue<string>();
            var refreshToken = node?["refresh_token"]?.GetValue<string>();
            var expiresIn = ReadInt(node?["expires_in"]) ?? 3600;

            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new RingAuthException("Ring devolvió una respuesta de autenticación no válida. Inténtalo de nuevo.");
            }

            return new RingTokens(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }
    }

    private static RingAuthException BuildAuthError(HttpStatusCode status, JsonNode? node, string rawBody, bool isRefresh)
    {
        var error = node?["error"]?.GetValue<string>();
        var description = node?["error_description"]?.GetValue<string>();

        if (status == HttpStatusCode.PreconditionFailed)
        {
            var tsv = node?["tsv_state"]?.GetValue<string>();
            var phone = node?["phone"]?.GetValue<string>();
            var prompt = tsv == "totp"
                ? "Introduce el código de 6 dígitos de tu aplicación de autenticación."
                : !string.IsNullOrWhiteSpace(phone)
                    ? $"Ring ha enviado un código a {phone}. Introdúcelo para continuar."
                    : "Ring ha enviado un código de verificación. Introdúcelo para continuar.";

            return new RingAuthException(prompt)
            {
                RequiresTwoFactor = true,
                Prompt = prompt,
                StatusCode = (int)status,
            };
        }

        if (status == HttpStatusCode.BadRequest && error?.StartsWith("Verification Code", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new RingAuthException("El código de verificación no es válido. Inténtalo de nuevo.")
            {
                RequiresTwoFactor = true,
                Prompt = "El código de verificación no es válido. Inténtalo de nuevo.",
                StatusCode = (int)status,
            };
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return new RingAuthException("Ring ha bloqueado temporalmente las solicitudes. Espera unos minutos e inténtalo de nuevo.")
            {
                StatusCode = (int)status,
            };
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            if (isRefresh)
            {
                return new RingAuthException("La sesión guardada ha caducado. Vuelve a iniciar sesión.")
                {
                    StatusCode = (int)status,
                };
            }

            return new RingAuthException("Correo o contraseña incorrectos.")
            {
                StatusCode = (int)status,
            };
        }

        var detail = !string.IsNullOrWhiteSpace(description) ? description : error;
        if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(rawBody) && rawBody.Length < 300)
        {
            detail = rawBody.Trim();
        }

        return new RingAuthException($"Ring rechazó la autenticación (HTTP {(int)status}){(string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}")}")
        {
            StatusCode = (int)status,
        };
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }
}
