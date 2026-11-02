using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace R8er.Api.Features.Broker;

/// Tenant-scoped signaling broker (design: specs/2026-11-01-r8er-gateway-pairing-
/// design.md). Connected gateways by gateway id, live browser sessions by session
/// id. The broker relays SDP/ICE but can't make either side trust the other:
/// pins + pairing do that. ponytail: in-memory, single API instance (Railway);
/// Postgres LISTEN/NOTIFY or Redis when there's a second instance.
public static class Broker
{
    public static readonly ConcurrentDictionary<Guid, Link> Gateways = new();      // gateway id → gateway
    public static readonly ConcurrentDictionary<Guid, Link> Sessions = new();    // session id → browser

    /// One end of a relay. Several readers may write to one socket (many browser
    /// sessions → one gateway), so sends are serialized.
    public sealed class Link(WebSocket socket, Guid tenantId, Guid gatewayId)
    {
        readonly SemaphoreSlim _send = new(1, 1);
        public WebSocket Socket => socket;
        public Guid TenantId => tenantId;
        public Guid GatewayId => gatewayId;

        public async Task SendAsync(JsonNode msg, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _send.WaitAsync(ct);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
            catch (Exception) { }   // the other end vanished; its own loop cleans up
            finally
            {
                _send.Release();
            }
        }
    }

    /// Next whole JSON text message, or null when the socket closes. Messages over
    /// max are skipped (SDP is a few KB; 64KB is generous).
    public static async Task<JsonObject?> ReadAsync(WebSocket socket, CancellationToken ct, int max = 64 * 1024)
    {
        var buffer = new byte[max];
        while (true)
        {
            int n = 0;
            var tooBig = false;
            ValueWebSocketReceiveResult r;
            do
            {
                if (n == buffer.Length)
                {
                    n = 0;
                    tooBig = true;
                }   // keep draining, then drop it
                r = await socket.ReceiveAsync(buffer.AsMemory(n), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return null;
                }
                n += r.Count;
            } while (!r.EndOfMessage);
            if (tooBig)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(buffer.AsSpan(0, n)) is JsonObject o)
                {
                    return o;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
    }

    public static async Task CloseAsync(WebSocket socket, string reason, CancellationToken ct)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, ct);
        }
        catch (Exception) { }
    }
}
