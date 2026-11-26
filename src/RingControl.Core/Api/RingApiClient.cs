using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RingControl.Core.Auth;
using RingControl.Core.Models;

namespace RingControl.Core.Api;

public sealed class RingApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }

    public RingApiException(string message, HttpStatusCode? statusCode = null, string? body = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }
}

/// <summary>
/// Client for Ring's private REST API. Ring has no public API; these endpoints are the
/// same ones used by the official mobile app and by community projects such as
/// python-ring-doorbell and ring-client-api.
/// </summary>
public sealed class RingApiClient : IDisposable
{
    private const string ClientApi = "https://api.ring.com/clients_api/";
    private const string DeviceApi = "https://api.ring.com/devices/v1/";
    private const string AppApi = "https://prd-api-us.prd.rings.solutions/api/v1/";
    private const string SnapshotApi = "https://app-snaps.ring.com/snapshots/next/";
    private const string UserAgent = "android:com.ringapp";

    private readonly HttpClient _http;
    private readonly RingAuthClient _auth;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private RingTokens _tokens;
    private bool _sessionCreated;

    public RingApiClient(HttpClient http, RingAuthClient auth, string hardwareId, RingTokens tokens)
    {
        _http = http;
        _auth = auth;
        HardwareId = hardwareId;
        _tokens = tokens;
    }

    public string HardwareId { get; }

    public RingTokens Tokens => _tokens;

    /// <summary>Raised whenever Ring rotates the refresh token so the caller can persist it.</summary>
    public event EventHandler<RingTokens>? TokensRefreshed;

    public async Task<IReadOnlyList<RingDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ClientApi + "ring_devices?api_version=11"), ct).ConfigureAwait(false);
        var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
        var devices = new List<RingDevice>();
        var seen = new HashSet<long>();

        AddArray(root?["doorbots"], fromDoorbellArray: true);
        AddArray(root?["stickup_cams"], fromDoorbellArray: false);
        AddArray(root?["cameras"], fromDoorbellArray: false);
        AddArray(root?["authorized_doorbots"], fromDoorbellArray: true);

        return devices
            .OrderByDescending(d => d.IsDoorbell)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void AddArray(JsonNode? array, bool fromDoorbellArray)
        {
            if (array is not JsonArray items)
            {
                return;
            }

            foreach (var item in items)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                var device = RingDevice.FromJson(obj, fromDoorbellArray);
                if (device.Id != 0 && seen.Add(device.Id))
                {
                    devices.Add(device);
                }
            }
        }
    }

    public async Task<RingHealth> GetHealthAsync(long deviceId, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ClientApi + $"doorbots/{deviceId}/health"), ct).ConfigureAwait(false);
        var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
        return RingHealth.FromJson(root?["device_health"] ?? root);
    }

    /// <summary>
    /// Fetches a fresh JPEG snapshot. Ring blocks snapshots while motion detection is
    /// disabled and can take several seconds to wake the camera, so this never waits
    /// longer than <paramref name="timeoutSeconds"/>.
    /// </summary>
    public async Task<byte[]?> GetSnapshotAsync(long deviceId, bool force, CancellationToken ct = default, int timeoutSeconds = 20)
    {
        var query = force ? "?extras=force" : string.Empty;
        using var request = new HttpRequestMessage(HttpMethod.Get, SnapshotApi + deviceId + query);
        request.Headers.TryAddWithoutValidation("Accept", "image/jpeg");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60)));

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(() => CloneRequest(request), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RingApiException(
                "La cámara no envió una instantánea a tiempo. Ring bloquea las instantáneas cuando la detección de movimiento está desactivada.",
                null,
                null,
                new TimeoutException());
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
    }

    public async Task<IReadOnlyList<RingEvent>> GetEventsAsync(RingDevice device, int days = 7, CancellationToken ct = default)
    {
        // The location events endpoint is the most reliable source: it works with and
        // without a Ring Protect plan and includes the recording/person metadata.
        if (!string.IsNullOrWhiteSpace(device.LocationId))
        {
            try
            {
                var eventsUrl = ClientApi + $"locations/{device.LocationId}/devices/{device.Id}/events?limit=30";
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, eventsUrl), ct).ConfigureAwait(false);
                var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
                var events = ParseLocationEvents(root);

                if (events.Count > 0)
                {
                    return events;
                }
            }
            catch (RingApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // Fall through to the other endpoints.
            }
        }

        var from = DateTimeOffset.UtcNow.AddDays(-Math.Abs(days)).ToUnixTimeMilliseconds();
        var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = ClientApi +
                  $"video_search/history?doorbot_id={device.Id}&date_from={from}&date_to={to}&order=desc&api_version=11&includes%5B%5D=pva";

        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
            var events = ParseVideoSearch(root);

            if (events.Count > 0)
            {
                return events;
            }
        }
        catch (RingApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Fall through to history.
        }

        try
        {
            var historyUrl = ClientApi + $"doorbots/{device.Id}/history?limit=30";
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, historyUrl), ct).ConfigureAwait(false);
            var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
            return ParseHistory(root);
        }
        catch (RingApiException)
        {
            return Array.Empty<RingEvent>();
        }
    }

    private static List<RingEvent> ParseLocationEvents(JsonNode? root)
    {
        var events = new List<RingEvent>();
        if (root?["events"] is not JsonArray array)
        {
            return events;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var ringEvent = RingEvent.FromLocationEvent(obj);
            if (!string.IsNullOrWhiteSpace(ringEvent.Id))
            {
                events.Add(ringEvent);
            }
        }

        return events;
    }

    private static List<RingEvent> ParseVideoSearch(JsonNode? root)
    {
        var events = new List<RingEvent>();
        if (root?["video_search"] is not JsonArray array)
        {
            return events;
        }

        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                var ringEvent = RingEvent.FromVideoSearch(obj);
                if (!string.IsNullOrWhiteSpace(ringEvent.Id))
                {
                    events.Add(ringEvent);
                }
            }
        }

        return events;
    }

    private static List<RingEvent> ParseHistory(JsonNode? root)
    {
        var events = new List<RingEvent>();
        if (root is not JsonArray array)
        {
            return events;
        }

        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                var ringEvent = RingEvent.FromHistory(obj);
                if (!string.IsNullOrWhiteSpace(ringEvent.Id))
                {
                    events.Add(ringEvent);
                }
            }
        }

        return events;
    }

    public async Task<IReadOnlyList<RingDing>> GetActiveDingsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ClientApi + "dings/active"), ct).ConfigureAwait(false);
        var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
        var dings = new List<RingDing>();
        var array = root as JsonArray ?? root?["dings"] as JsonArray;

        if (array is null)
        {
            return dings;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var id = obj.Str("id") ?? obj.Str("ding_id_str");
            var deviceId = obj.Long("doorbot_id") ?? obj.Long("doorbot_id_str") ?? 0;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            dings.Add(new RingDing(
                id,
                deviceId,
                obj.Str("kind") ?? "motion",
                obj.Date("created_at") ?? DateTimeOffset.UtcNow));
        }

        return dings;
    }

    public async Task<string> GetSignalTicketAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, AppApi + "clap/ticket/request/signalsocket");
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            return request;
        }, ct).ConfigureAwait(false);

        var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
        var ticket = root?["ticket"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(ticket))
        {
            throw new RingApiException("Ring no devolvió un ticket para iniciar el vídeo en directo.");
        }

        return ticket;
    }

    public Task SetLightAsync(long deviceId, bool on, CancellationToken ct = default)
    {
        var url = ClientApi + $"doorbots/{deviceId}/floodlight_light_{(on ? "on" : "off")}";
        return SendNoContentAsync(() => new HttpRequestMessage(HttpMethod.Put, url), ct);
    }

    public Task SetSirenAsync(long deviceId, bool on, CancellationToken ct = default)
    {
        var url = ClientApi + $"doorbots/{deviceId}/siren_{(on ? "on" : "off")}";
        return SendNoContentAsync(() => new HttpRequestMessage(HttpMethod.Put, url), ct);
    }

    public Task SetChimeAsync(long deviceId, bool on, CancellationToken ct = default)
    {
        return UpdateSettingsAsync(deviceId, new JsonObject
        {
            ["chime_settings"] = new JsonObject { ["enable"] = on },
        }, ct);
    }

    public Task SetMotionDetectionAsync(long deviceId, bool on, CancellationToken ct = default)
    {
        return UpdateSettingsAsync(deviceId, new JsonObject
        {
            ["motion_detection_enabled"] = on,
        }, ct);
    }

    private async Task UpdateSettingsAsync(long deviceId, JsonObject settings, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["doorbot"] = new JsonObject
            {
                ["settings"] = settings,
            },
        };

        await SendNoContentAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, ClientApi + $"doorbots/{deviceId}");
            request.Content = JsonContent.Create(body);
            return request;
        }, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetClipUrlAsync(string dingId, CancellationToken ct = default)
    {
        var url = ClientApi + $"dings/{Uri.EscapeDataString(dingId)}/share/play?disable_redirect=true";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        var root = await ParseJsonAsync(response, ct).ConfigureAwait(false);
        return root?["url"]?.GetValue<string>();
    }

    public async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct = default)
    {
        using var response = await GetExternalAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var response = await GetExternalAsync(url, ct).ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)copied / total, 0, 1));
            }
        }

        progress?.Report(1);
    }

    private async Task<HttpResponseMessage> GetExternalAsync(string url, CancellationToken ct)
    {
        if (url.Contains("api.ring.com", StringComparison.OrdinalIgnoreCase))
        {
            return await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        }

        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new RingApiException($"No se pudo descargar el archivo (HTTP {(int)status}).", status);
        }

        return response;
    }

    private async Task SendNoContentAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        using var response = await SendAsync(requestFactory, ct).ConfigureAwait(false);
        // Drain the response so the connection can be reused.
        _ = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct, bool isSessionRequest = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureTokenAsync(ct).ConfigureAwait(false);

            if (!isSessionRequest)
            {
                await EnsureSessionAsync(ct).ConfigureAwait(false);
            }

            var request = requestFactory();
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _tokens.AccessToken);
            request.Headers.TryAddWithoutValidation("hardware_id", HardwareId);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RingApiException("No se pudo conectar con los servidores de Ring.", null, null, ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                await RefreshTokensAsync(force: true, ct).ConfigureAwait(false);
                continue;
            }

            if ((int)response.StatusCode == 429 && attempt < 2)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                response.Dispose();
                await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct).ConfigureAwait(false);
                continue;
            }

            if ((int)response.StatusCode >= 500 && attempt < 1)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
                var status = response.StatusCode;
                response.Dispose();
                throw new RingApiException(BuildApiError(status, body), status, body);
            }

            return response;
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _tokens.ExpiresAt.AddMinutes(-2))
        {
            return;
        }

        await RefreshTokensAsync(force: false, ct).ConfigureAwait(false);
    }

    private async Task RefreshTokensAsync(bool force, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && DateTimeOffset.UtcNow < _tokens.ExpiresAt.AddMinutes(-2))
            {
                return;
            }

            var refreshed = await _auth.RefreshAsync(_tokens.RefreshToken, ct).ConfigureAwait(false);
            _tokens = refreshed;
            _sessionCreated = false;
            TokensRefreshed?.Invoke(this, refreshed);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionCreated)
        {
            return;
        }

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionCreated)
            {
                return;
            }

            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ClientApi + "session");
                request.Content = JsonContent.Create(new
                {
                    device = new
                    {
                        hardware_id = HardwareId,
                        metadata = new
                        {
                            api_version = 11,
                            device_model = "RingControl WPF",
                        },
                        os = "windows",
                    },
                });
                return request;
            }, ct, isSessionRequest: true).ConfigureAwait(false);

            _ = await SafeReadAsync(response, ct).ConfigureAwait(false);
            _sessionCreated = true;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private static async Task<JsonNode?> ParseJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            throw new RingApiException("Ring devolvió una respuesta que no se pudo interpretar.", response.StatusCode, text, ex);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildApiError(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            var node = JsonNode.Parse(body);
            detail = node?["error_description"]?.GetValue<string>()
                     ?? node?["error"]?.GetValue<string>()
                     ?? node?["message"]?.GetValue<string>();

            if (detail is null && node?["errors"] is JsonArray errors && errors.Count > 0)
            {
                detail = string.Join(", ", errors.Select(e => e?.ToJsonString()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }
        catch
        {
            // Not JSON.
        }

        if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(body) && body.Length < 200)
        {
            detail = body.Trim();
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "La sesión con Ring ha caducado. Vuelve a iniciar sesión.",
            HttpStatusCode.Forbidden => "Ring ha denegado esta operación para tu cuenta.",
            HttpStatusCode.NotFound => "Ring no encontró el dispositivo o el recurso solicitado.",
            HttpStatusCode.TooManyRequests => "Ring ha limitado las solicitudes. Espera un momento.",
            _ => $"Ring devolvió un error (HTTP {(int)status}){(string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}")}",
        };
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
        _sessionLock.Dispose();
    }
}
