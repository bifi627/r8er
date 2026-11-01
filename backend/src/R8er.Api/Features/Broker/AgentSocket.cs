using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using R8er.Api.Data;
using R8er.Api.Features.Onboarding;

namespace R8er.Api.Features.Broker;

/// The agent's control socket, authenticated by its credential (Bearer):
/// heartbeats (last_seen_at + advertised LAN URL) and the agent's half of
/// brokered browser sessions (answer/ice back to the session's browser).
public static class AgentSocket
{
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
            var me = new Broker.Link(socket, device.TenantId, device.Id);
            Broker.Agents[device.Id] = me;   // a reconnect replaces the stale link
            var ct = ctx.RequestAborted;
            try
            {
                while (await Broker.ReadAsync(socket, ct) is { } msg)
                {
                    switch ((string?)msg["type"])
                    {
                        case "heartbeat":
                            var lanUrl = SafeLanUrl((string?)msg["lanUrl"]);
                            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s
                                .SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow)
                                .SetProperty(d => d.LanUrl, lanUrl), ct);
                            break;
                        case "answer" or "ice":
                            // Only into sessions opened *for this device*: an agent can't
                            // inject signaling into another agent's (or tenant's) session.
                            if (Guid.TryParse((string?)msg["session"], out var sid)
                                && Broker.Sessions.TryGetValue(sid, out var browser)
                                && browser.DeviceId == device.Id)
                                await browser.SendAsync(Relay(msg), ct);
                            break;
                    }
                }
            }
            catch (Exception) { }   // a dropped agent socket must never crash the process
            finally { Broker.Agents.TryRemove(new(device.Id, me)); }
        });

    // Forward only the fields the protocol defines (docs/protocol.md).
    static JsonObject Relay(JsonObject m) => new()
    {
        ["type"] = (string?)m["type"],
        ["sdp"] = m["sdp"]?.DeepClone(),
        ["candidate"] = m["candidate"]?.DeepClone(),
    };

    // The web app renders this as a link: only plain http(s) URLs, never javascript: etc.
    static string? SafeLanUrl(string? s) =>
        s is { Length: <= 200 } && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? u.ToString() : null;
}
