using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace R8er.Api;

/// POC signaling stub (implementation-plan Phase 0, step 2): rendezvous two
/// peers by room id and relay their SDP/ICE frames verbatim over the real
/// internet. No auth, no tenancy — MVP step 3 promotes this into the
/// tenant-isolated broker. ponytail: relay opaque frames, never parse SDP.
public static class SignalingHub
{
    // room id -> the (<=2) sockets currently in it
    private static readonly ConcurrentDictionary<string, Room> Rooms = new();

    public static async Task RunAsync(string room, WebSocket socket, CancellationToken ct)
    {
        var r = Rooms.GetOrAdd(room, _ => new Room());
        if (!r.TryJoin(socket))
        {
            // ponytail: POC is exactly two peers per room; reject the third.
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "room full", ct);
            return;
        }

        // ponytail: forward each receive with its own EndOfMessage flag, so a
        // frame larger than the buffer relays as multiple frames — no reassembly.
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var msg = await socket.ReceiveAsync(buffer, ct);
                if (msg.MessageType == WebSocketMessageType.Close) break;

                // Only the other peer's read loop writes to a given socket, so
                // there is exactly one writer per socket — SendAsync stays safe.
                var peer = r.Other(socket);
                if (peer is { State: WebSocketState.Open })
                    await peer.SendAsync(
                        buffer.AsMemory(0, msg.Count),
                        msg.MessageType, msg.EndOfMessage, ct);
            }
        }
        catch (OperationCanceledException) { /* server shutdown / client abort */ }
        catch (WebSocketException) { /* peer vanished mid-frame */ }
        finally
        {
            r.Leave(socket);
            if (r.IsEmpty) Rooms.TryRemove(room, out _);
        }
    }

    private sealed class Room
    {
        private readonly WebSocket?[] _slots = new WebSocket?[2];
        private readonly Lock _lock = new();

        public bool TryJoin(WebSocket s)
        {
            lock (_lock)
            {
                for (var i = 0; i < 2; i++)
                    if (_slots[i] is null) { _slots[i] = s; return true; }
                return false;
            }
        }

        public WebSocket? Other(WebSocket s)
        {
            lock (_lock)
                return _slots[0] == s ? _slots[1] : _slots[0];
        }

        public void Leave(WebSocket s)
        {
            lock (_lock)
                for (var i = 0; i < 2; i++)
                    if (_slots[i] == s) _slots[i] = null;
        }

        public bool IsEmpty
        {
            get { lock (_lock) return _slots[0] is null && _slots[1] is null; }
        }
    }
}
