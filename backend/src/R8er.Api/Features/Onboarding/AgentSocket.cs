using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using R8er.Api.Data;

namespace R8er.Api.Features.Onboarding;

/// The agent's control socket, authenticated by its credential (Bearer). PR A:
/// heartbeats (last_seen_at + advertised LAN URL); PR B adds signaling.
public static class AgentSocket
{
    record Heartbeat(string? Type, string? LanUrl);

    public static void Map(IEndpointRouteBuilder app) =>
        app.Map("/agents/ws", async (HttpContext ctx, R8erDbContext db) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
            var hash = Secrets.Hash(credential);
            // Tenant-filter exception 2 of 2: the credential establishes the tenant.
            var device = credential.Length == 0 ? null
                : await db.Devices.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.AgentCredentialHash == hash, ctx.RequestAborted);
            if (device is null) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

            db.CurrentTenantId = device.TenantId;   // filter live from here on
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var msg = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (msg.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ctx.RequestAborted);
                        break;
                    }
                    if (!msg.EndOfMessage) continue;   // ponytail: heartbeats are tiny; drop oversized frames
                    var hb = TryParse(buffer.AsSpan(0, msg.Count));
                    if (hb?.Type != "heartbeat") continue;
                    var lanUrl = SafeLanUrl(hb.LanUrl);
                    await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow)
                        .SetProperty(d => d.LanUrl, lanUrl), ctx.RequestAborted);
                }
            }
            catch (Exception) { }   // a dropped agent socket must never crash the process
        });

    static Heartbeat? TryParse(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize<Heartbeat>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    // The web app renders this as a link: only plain http(s) URLs, never javascript: etc.
    static string? SafeLanUrl(string? s) =>
        s is { Length: <= 200 } && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? u.ToString() : null;
}
