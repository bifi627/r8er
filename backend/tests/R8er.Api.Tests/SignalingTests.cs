using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace R8er.Api.Tests;

public class SignalingTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<WebSocket> Connect(string room)
    {
        var client = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"signaling/{room}");
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> Receive(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        return Encoding.UTF8.GetString(buf, 0, r.Count);
    }

    [Fact]
    public async Task Relays_a_frame_between_two_peers_in_a_room()
    {
        using var a = await Connect("r1");
        using var b = await Connect("r1");

        await Send(a, "OFFER-SDP");

        Assert.Equal("OFFER-SDP", await Receive(b));
    }

    [Fact]
    public async Task Does_not_leak_across_rooms()
    {
        using var a1 = await Connect("roomA");
        using var a2 = await Connect("roomA");
        using var b = await Connect("roomB");

        await Send(a1, "roomA-only");

        // The roomA peer receives it...
        Assert.Equal("roomA-only", await Receive(a2));

        // ...the roomB peer never does.
        var buf = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => b.ReceiveAsync(buf, cts.Token));
    }

    [Fact]
    public async Task Rejects_a_third_peer()
    {
        using var a = await Connect("full");
        using var b = await Connect("full");
        using var c = await Connect("full");

        var buf = new byte[1024];
        var r = await c.ReceiveAsync(buf, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
    }
}
