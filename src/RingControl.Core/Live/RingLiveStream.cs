using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using RingControl.Core.Api;
using RingControl.Core.Models;
using SIPSorcery.Net;

namespace RingControl.Core.Live;

public enum LiveStreamState
{
    Idle,
    RequestingTicket,
    Connecting,
    Negotiating,
    WaitingForVideo,
    Streaming,
    Stopped,
    Failed,
}

public sealed class LiveStreamStatusEventArgs : EventArgs
{
    public LiveStreamStatusEventArgs(LiveStreamState state, string message)
    {
        State = state;
        Message = message;
    }

    public LiveStreamState State { get; }
    public string Message { get; }
}

/// <summary>
/// Live view implementation for Ring cameras. It talks to Ring's signalling WebSocket,
/// negotiates a WebRTC session with SIPSorcery (video only) and forwards the decrypted RTP
/// to a local UDP port where VLC renders it. RTP H264 payloads are also exposed so they can
/// be recorded without re-encoding.
/// </summary>
public sealed class RingLiveStream : IAsyncDisposable
{
    private static readonly RTCIceServer[] IceServers =
    {
        new() { urls = "stun:stun.kinesisvideo.us-east-1.amazonaws.com:443" },
        new() { urls = "stun:stun.kinesisvideo.us-east-2.amazonaws.com:443" },
        new() { urls = "stun:stun.kinesisvideo.us-west-2.amazonaws.com:443" },
        new() { urls = "stun:stun.l.google.com:19302" },
        new() { urls = "stun:stun1.l.google.com:19302" },
        new() { urls = "stun:stun2.l.google.com:19302" },
        new() { urls = "stun:stun3.l.google.com:19302" },
        new() { urls = "stun:stun4.l.google.com:19302" },
    };

    private readonly RingApiClient _api;
    private readonly RingDevice _device;
    private readonly Action<string>? _log;
    private readonly string _dialogId = Guid.NewGuid().ToString();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<(string Candidate, int MLine)> _pendingRemoteIce = new();
    private readonly List<RTCIceCandidate> _pendingLocalIce = new();
    private readonly TaskCompletionSource<string> _answerTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private RTCPeerConnection? _pc;
    private UdpClient? _udp;
    private string? _sessionId;
    private string? _ticket;
    private bool _offerSent;
    private bool _remoteDescriptionSet;
    private bool _activated;
    private int _stopRequested;
    private LiveStreamState _state = LiveStreamState.Idle;
    private int _firstVideoPacket;
    private long _packetCount;
    private long _byteCount;
    private uint _remoteSsrc;
    private Task? _receiveTask;
    private Task? _pingTask;
    private Task? _pliTask;

    public RingLiveStream(RingApiClient api, RingDevice device, Action<string>? log = null)
    {
        _api = api;
        _device = device;
        _log = log;
    }

    public event EventHandler<LiveStreamStatusEventArgs>? StatusChanged;

    public event EventHandler? FirstVideoPacket;

    /// <summary>Raised on the network thread with each decrypted H264 RTP payload and its RTP timestamp.</summary>
    public event Action<byte[], uint>? VideoPayload;

    public LiveStreamState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string? SdpFilePath { get; private set; }

    public int LocalVideoPort { get; private set; }

    public int VideoPayloadType { get; private set; }

    /// <summary>Set to true once the local player has started, so RTP is forwarded.</summary>
    public bool ForwardEnabled { get; set; }

    public long PacketCount => Interlocked.Read(ref _packetCount);

    public long ByteCount => Interlocked.Read(ref _byteCount);

    public TimeSpan Uptime => _startedAt is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - _startedAt.Value;

    private DateTimeOffset? _startedAt;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_ws is not null)
        {
            throw new InvalidOperationException("La sesión en directo ya está iniciada.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startedAt = DateTimeOffset.UtcNow;
        var ct = _cts.Token;

        try
        {
            SetState(LiveStreamState.RequestingTicket, "Solicitando acceso al vídeo…");
            _ticket = await _api.GetSignalTicketAsync(ct).ConfigureAwait(false);
            Log("Ticket de señalización obtenido.");

            SetState(LiveStreamState.Connecting, "Conectando con Ring…");
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("User-Agent", "android:com.ringapp");
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            var uri = new Uri(
                "wss://api.prod.signalling.ring.devices.a2z.com:443/ws" +
                "?api_version=4.0&auth_type=ring_solutions" +
                $"&client_id=ring_site-{Guid.NewGuid()}" +
                $"&token={Uri.EscapeDataString(_ticket)}");

            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            Log("WebSocket de señalización conectado.");
            _receiveTask = Task.Run(() => ReceiveLoopAsync(ct), CancellationToken.None);

            SetState(LiveStreamState.Negotiating, "Negociando el vídeo…");
            CreatePeerConnection();

            var offer = _pc!.createOffer();
            await _pc.setLocalDescription(offer).ConfigureAwait(false);

            await SendJsonAsync(new JsonObject
            {
                ["method"] = "live_view",
                ["dialog_id"] = _dialogId,
                ["body"] = new JsonObject
                {
                    ["doorbot_id"] = _device.Id,
                    ["stream_options"] = new JsonObject
                    {
                        ["audio_enabled"] = false,
                        ["video_enabled"] = true,
                    },
                    ["sdp"] = offer.sdp,
                },
            }, ct).ConfigureAwait(false);

            _offerSent = true;
            await FlushLocalIceAsync(ct).ConfigureAwait(false);

            var answer = await WithTimeoutAsync(_answerTcs.Task, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            Log("Respuesta SDP recibida.");

            _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answer,
            });

            _remoteDescriptionSet = true;
            ApplyPendingRemoteIce();

            LocalVideoPort = LiveSdp.ReserveUdpPort();
            var (sdp, payloadType, fmtp) = LiveSdp.Build(answer, LocalVideoPort);
            VideoPayloadType = payloadType;
            SdpFilePath = Path.Combine(Path.GetTempPath(), $"ring-live-{Guid.NewGuid():N}.sdp");
            await File.WriteAllTextAsync(SdpFilePath, sdp, ct).ConfigureAwait(false);
            Log($"SDP local preparado (puerto {LocalVideoPort}, payload {payloadType}{(string.IsNullOrWhiteSpace(fmtp) ? string.Empty : ", " + fmtp)}).");

            await TryActivateAsync(ct).ConfigureAwait(false);
            StartLoops(ct);

            SetState(LiveStreamState.WaitingForVideo, "Esperando la imagen de la cámara…");
        }
        catch (Exception ex)
        {
            Log("Error al iniciar el vídeo: " + ex.Message);
            SetState(LiveStreamState.Failed, DescribeError(ex));
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void CreatePeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = IceServers.ToList(),
            X_UseRtpFeedbackProfile = true,
        };

        _pc = new RTCPeerConnection(config);

        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            96,
            "H264",
            90000,
            0,
            "packetization-mode=1;profile-level-id=640029;level-asymmetry-allowed=1");

        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            false,
            new List<SDPAudioVideoMediaFormat> { h264 },
            MediaStreamStatusEnum.RecvOnly);

        _pc.addTrack(videoTrack);

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null)
            {
                return;
            }

            if (!_offerSent)
            {
                lock (_stateLock)
                {
                    _pendingLocalIce.Add(candidate);
                }

                return;
            }

            _ = SendIceAsync(candidate);
        };

        _pc.onconnectionstatechange += state =>
        {
            Log("Estado WebRTC: " + state);
            if (state == RTCPeerConnectionState.connected)
            {
                SetState(LiveStreamState.Streaming, "Vídeo en directo");
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                SetState(LiveStreamState.Failed, "No se pudo establecer la conexión de vídeo (ICE).");
                _answerTcs.TrySetException(new InvalidOperationException("La conexión ICE con Ring ha fallado."));
                _ = StopAsync();
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                Log("Estado WebRTC: closed" + (_stopRequested == 0 ? " (cierre inesperado)" : string.Empty));
                SetState(LiveStreamState.Stopped, "Vídeo detenido");
            }
        };

        _pc.OnRtpPacketReceived += OnRtpPacketReceived;

        _udp = new UdpClient(AddressFamily.InterNetwork);
    }

    private async Task SendIceAsync(RTCIceCandidate candidate)
    {
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            await SendJsonAsync(new JsonObject
            {
                ["method"] = "ice",
                ["dialog_id"] = _dialogId,
                ["body"] = new JsonObject
                {
                    ["doorbot_id"] = _device.Id,
                    ["ice"] = candidate.candidate,
                    ["mlineindex"] = candidate.sdpMLineIndex,
                },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("No se pudo enviar un candidato ICE: " + ex.Message);
        }
    }

    private async Task FlushLocalIceAsync(CancellationToken ct)
    {
        List<RTCIceCandidate> pending;
        lock (_stateLock)
        {
            pending = _pendingLocalIce.ToList();
            _pendingLocalIce.Clear();
        }

        foreach (var candidate in pending)
        {
            await SendIceAsync(candidate).ConfigureAwait(false);
        }
    }

    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.video || packet is null)
        {
            return;
        }

        if (VideoPayloadType != 0 && packet.Header.PayloadType != VideoPayloadType)
        {
            // Ignore RTX / FEC payloads.
            return;
        }

        Interlocked.Increment(ref _packetCount);
        Interlocked.Add(ref _byteCount, packet.Payload.Length);
        _remoteSsrc = packet.Header.SyncSource;

        if (Interlocked.Exchange(ref _firstVideoPacket, 1) == 0)
        {
            Log("Primer paquete de vídeo recibido.");
            SetState(LiveStreamState.Streaming, "Vídeo en directo");
            FirstVideoPacket?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            VideoPayload?.Invoke(packet.Payload, packet.Header.Timestamp);
        }
        catch (Exception ex)
        {
            Log("Error procesando un paquete H264: " + ex.Message);
        }

        if (!ForwardEnabled || LocalVideoPort <= 0 || _udp is null)
        {
            return;
        }

        try
        {
            var bytes = packet.GetBytes();
            _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, LocalVideoPort));
        }
        catch
        {
            // The local player may not be listening yet; drop the packet.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (message.Length == 0)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                await HandleMessageAsync(json, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            if (_stopRequested == 0)
            {
                Log("El WebSocket de señalización se cerró: " + ex.Message);
                SetState(LiveStreamState.Failed, "Se perdió la conexión con Ring.");
            }
        }
        finally
        {
            if (_stopRequested == 0 && State is not (LiveStreamState.Failed or LiveStreamState.Stopped))
            {
                SetState(LiveStreamState.Stopped, "Vídeo detenido");
            }
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return;
        }

        var method = root?["method"]?.GetValue<string>();
        var body = root?["body"] as JsonObject;
        if (method is null || body is null)
        {
            return;
        }

        var doorbotId = body.Long("doorbot_id");
        if (doorbotId is not null && doorbotId != _device.Id)
        {
            return;
        }

        var sessionId = body.Str("session_id");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (_sessionId is null)
            {
                _sessionId = sessionId;
                Log("Sesión de vídeo creada: " + sessionId);
                await TryActivateAsync(ct).ConfigureAwait(false);
            }
            else if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }
        }

        switch (method)
        {
            case "session_created":
            case "session_started":
                return;

            case "sdp":
            {
                var sdp = body.Str("sdp");
                if (!string.IsNullOrWhiteSpace(sdp))
                {
                    _answerTcs.TrySetResult(sdp);
                }

                return;
            }

            case "ice":
            {
                var ice = body.Str("ice");
                var mline = (int)(body.Long("mlineindex") ?? 0);
                if (!string.IsNullOrWhiteSpace(ice))
                {
                    if (_remoteDescriptionSet)
                    {
                        AddRemoteIce(ice, mline);
                    }
                    else
                    {
                        lock (_stateLock)
                        {
                            _pendingRemoteIce.Add((ice, mline));
                        }
                    }
                }

                return;
            }

            case "ping":
                await SendSessionMessageAsync("pong", null, ct).ConfigureAwait(false);
                return;

            case "pong":
                return;

            case "notification":
            {
                var text = body.Str("text");
                Log("Notificación de Ring: " + text);
                if (string.Equals(text, "camera_connected", StringComparison.OrdinalIgnoreCase))
                {
                    SetState(LiveStreamState.Streaming, "Cámara conectada");
                }

                return;
            }

            case "close":
            {
                var reason = body.Node("reason")?.Str("text");
                var message = string.IsNullOrWhiteSpace(reason)
                    ? "Ring cerró la sesión de vídeo."
                    : "Ring cerró la sesión: " + reason;
                Log(message);
                SetState(LiveStreamState.Failed, message);
                _answerTcs.TrySetException(new InvalidOperationException(message));
                return;
            }

            case "camera_started":
            case "stream_info":
                return;

            default:
                Log("Mensaje de señalización no reconocido: " + method);
                return;
        }
    }

    private void AddRemoteIce(string candidate, int mlineIndex)
    {
        try
        {
            _pc?.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMLineIndex = (ushort)Math.Max(0, mlineIndex),
            });
        }
        catch (Exception ex)
        {
            Log("No se pudo aplicar un candidato ICE remoto: " + ex.Message);
        }
    }

    private void ApplyPendingRemoteIce()
    {
        List<(string Candidate, int MLine)> pending;
        lock (_stateLock)
        {
            pending = _pendingRemoteIce.ToList();
            _pendingRemoteIce.Clear();
        }

        foreach (var (candidate, mline) in pending)
        {
            AddRemoteIce(candidate, mline);
        }
    }

    private async Task TryActivateAsync(CancellationToken ct)
    {
        if (_activated || !_remoteDescriptionSet || string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        _activated = true;
        Log("Activando la sesión de vídeo.");
        await SendSessionMessageAsync("activate_session", null, ct).ConfigureAwait(false);
        await SendSessionMessageAsync("stream_options", new JsonObject
        {
            ["audio_enabled"] = false,
            ["video_enabled"] = true,
        }, ct).ConfigureAwait(false);
    }

    private async Task SendSessionMessageAsync(string method, JsonObject? extraBody, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        var body = new JsonObject
        {
            ["doorbot_id"] = _device.Id,
            ["session_id"] = _sessionId,
        };

        if (extraBody is not null)
        {
            foreach (var pair in extraBody)
            {
                body[pair.Key] = pair.Value?.DeepClone();
            }
        }

        await SendJsonAsync(new JsonObject
        {
            ["method"] = method,
            ["dialog_id"] = _dialogId,
            ["body"] = body,
        }, ct).ConfigureAwait(false);
    }

    private async Task SendJsonAsync(JsonObject payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void StartLoops(CancellationToken ct)
    {
        _pingTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    await SendSessionMessageAsync("ping", null, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log("Bucle de ping detenido: " + ex.Message);
            }
        }, CancellationToken.None);

        _pliTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    RequestKeyFrame();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log("Bucle de keyframe detenido: " + ex.Message);
            }
        }, CancellationToken.None);
    }

    private void RequestKeyFrame()
    {
        var ssrc = _remoteSsrc;
        if (ssrc == 0 || _pc is null)
        {
            return;
        }

        try
        {
            _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(0, ssrc, PSFBFeedbackTypesEnum.PLI));
        }
        catch
        {
            // Ignore RTCP errors; the camera will eventually send a keyframe.
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        Log("Deteniendo la sesión de vídeo.");

        try
        {
            try
            {
                var ct = _cts?.Token ?? CancellationToken.None;
                if (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    await SendSessionMessageAsync("close", new JsonObject
                    {
                        ["reason"] = new JsonObject { ["code"] = 0, ["text"] = string.Empty },
                    }, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best effort.
            }

            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                if (_ws?.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                _ws?.Dispose();
            }
            catch
            {
            }

            _ws = null;

            try
            {
                _pc?.close();
            }
            catch
            {
            }

            _pc = null;

            try
            {
                _udp?.Dispose();
            }
            catch
            {
            }

            _udp = null;

            if (SdpFilePath is not null)
            {
                try
                {
                    File.Delete(SdpFilePath);
                }
                catch
                {
                }

                SdpFilePath = null;
            }

            if (State is not LiveStreamState.Failed)
            {
                SetState(LiveStreamState.Stopped, "Vídeo detenido");
            }
        }
        finally
        {
            _sendLock.Dispose();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetState(LiveStreamState state, string message)
    {
        lock (_stateLock)
        {
            if (_state == state && state is LiveStreamState.Streaming or LiveStreamState.WaitingForVideo)
            {
                return;
            }

            _state = state;
        }

        StatusChanged?.Invoke(this, new LiveStreamStatusEventArgs(state, message));
    }

    private void Log(string message) => _log?.Invoke(message);

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed == task)
        {
            return await task.ConfigureAwait(false);
        }

        throw new TimeoutException("Ring no respondió a la solicitud de vídeo a tiempo.");
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        TimeoutException => "Ring no respondió a tiempo. Inténtalo de nuevo.",
        RingApiException api => api.Message,
        WebSocketException => "No se pudo conectar con el servicio de vídeo de Ring.",
        _ => "No se pudo iniciar el vídeo: " + ex.Message,
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

