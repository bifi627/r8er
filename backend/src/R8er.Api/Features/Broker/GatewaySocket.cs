using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using R8er.Api.Data;
using R8er.Api.Features.Onboarding;

namespace R8er.Api.Features.Broker;

/// The gateway's control socket, authenticated by its credential (Bearer):
/// heartbeats (last_seen_at + advertised LAN URL) and the gateway's half of
/// brokered browser sessions (answer/ice back to the session's browser).
public static class GatewaySocket
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map("/gateways/ws", async (HttpContext ctx, R8erDbContext db) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
            var hash = Secrets.Hash(credential);
            // Tenant-filter exception 2 of 2: the credential establishes the tenant.
            var gateway = credential.Length == 0 ? null
                : await db.Gateways.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.CredentialHash == hash, ctx.RequestAborted);
            if (gateway is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            db.CurrentTenantId = gateway.TenantId;   // filter live from here on
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var me = new Broker.Link(socket, gateway.TenantId, gateway.Id);
            Broker.Gateways[gateway.Id] = me;   // a reconnect replaces the stale link
            var ct = ctx.RequestAborted;
            try
            {
                while (await Broker.ReadAsync(socket, ct) is { } msg)
                {
                    switch ((string?)msg["type"])
                    {
                        case "heartbeat":
                            var lanUrl = SafeLanUrl((string?)msg["lanUrl"]);
                            await db.Gateways.Where(d => d.Id == gateway.Id).ExecuteUpdateAsync(s => s
                                .SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow)
                                .SetProperty(d => d.LanUrl, lanUrl), ct);
                            break;
                        case "answer" or "ice":
                            // Only into sessions opened *for this gateway*: a gateway can't
                            // inject signaling into another gateway's (or tenant's) session.
                            if (Guid.TryParse((string?)msg["session"], out var sid)
                                && Broker.Sessions.TryGetValue(sid, out var browser)
                                && browser.GatewayId == gateway.Id)
                            {
                                await browser.SendAsync(Relay(msg), ct);
                            }

                            break;
                    }
                }
            }
            catch (Exception) { }   // a dropped gateway socket must never crash the process
            finally
            {
                Broker.Gateways.TryRemove(new(gateway.Id, me));
            }
        });
    }

    // Forward only the fields the protocol defines (docs/protocol.md).
    static JsonObject Relay(JsonObject m)
    {
        return new()
        {
            ["type"] = (string?)m["type"],
            ["sdp"] = m["sdp"]?.DeepClone(),
            ["candidate"] = m["candidate"]?.DeepClone(),
        };
    }

    // The web app renders this as a link: only plain http(s) URLs, never javascript: etc.
    static string? SafeLanUrl(string? s)
    {
        return s is { Length: <= 200 } && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? u.ToString() : null;
    }
}
