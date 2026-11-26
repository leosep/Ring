using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RingControl.Core.Live;

/// <summary>
/// Builds a minimal plain-RTP SDP for a local player (VLC) from the SDP answer returned by
/// Ring's WebRTC signalling server. Ring's answer uses DTLS-SRTP, but SIPSorcery terminates
/// the DTLS/SRTP leg for us, so the player only needs to know the H264 payload type and the
/// UDP port it should listen on.
/// </summary>
public static class LiveSdp
{
    public static (string Sdp, int PayloadType, string? Fmtp) Build(string answerSdp, int localPort)
    {
        var lines = answerSdp.Replace("\r\n", "\n").Split('\n');
        var videoIndex = Array.FindIndex(lines, line => line.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase));
        if (videoIndex < 0)
        {
            throw new InvalidOperationException("Ring did not negotiate a video track for this device.");
        }

        var mediaParts = lines[videoIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var payloadTypes = mediaParts.Skip(3).ToArray();
        string? selected = null;
        string? fmtp = null;

        foreach (var pt in payloadTypes)
        {
            var rtpmap = lines.FirstOrDefault(line => line.StartsWith($"a=rtpmap:{pt} ", StringComparison.OrdinalIgnoreCase));
            if (rtpmap is not null && rtpmap.Contains("H264", StringComparison.OrdinalIgnoreCase))
            {
                selected = pt;
                fmtp = lines.FirstOrDefault(line => line.StartsWith($"a=fmtp:{pt} ", StringComparison.OrdinalIgnoreCase))?
                    .Substring($"a=fmtp:{pt} ".Length);
                break;
            }
        }

        if (selected is null)
        {
            // Some firmware only advertises the format in the m-line. Fall back to the first
            // payload type and let the player do its best.
            selected = payloadTypes.FirstOrDefault();
        }

        if (selected is null || !int.TryParse(selected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var payloadType))
        {
            throw new InvalidOperationException("Could not determine Ring's video format.");
        }

        var builder = new StringBuilder();
        builder.Append("v=0\r\n");
        builder.Append("o=- 0 0 IN IP4 127.0.0.1\r\n");
        builder.Append("s=Ring Control Live View\r\n");
        builder.Append("c=IN IP4 0.0.0.0\r\n");
        builder.Append("t=0 0\r\n");
        builder.Append($"m=video {localPort} RTP/AVP {payloadType}\r\n");
        builder.Append($"a=rtpmap:{payloadType} H264/90000\r\n");
        if (!string.IsNullOrWhiteSpace(fmtp))
        {
            builder.Append($"a=fmtp:{payloadType} {fmtp.Trim()}\r\n");
        }

        builder.Append("a=recvonly\r\n");

        return (builder.ToString(), payloadType, fmtp);
    }

    /// <summary>Reserves an ephemeral UDP port and releases it so VLC can bind it.</summary>
    public static int ReserveUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
