using System.Text.Json.Nodes;
using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using StreamIq.Api.Auth;
using StreamIq.Api.Data;

namespace StreamIq.Api.Features.Broker;

/// A browser's signaling socket: one WebSocket = one session to one gateway. The
/// Firebase ID token arrives as the first message (not a query param — keeps it
/// out of logs); the offer may only target an online gateway of the caller's tenant.
public static class BrowserSocket
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map("/broker/ws", async (HttpContext ctx, StreamIqDbContext db, UserProvisioner provisioner) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var ct = ctx.RequestAborted;
            Guid session = Guid.Empty;
            Broker.Link? gateway = null;
            try
            {
                // Same verify path as the HTTP API (FirebaseAuthHandler), no bypass.
                if (await Broker.ReadAsync(socket, ct, 8 * 1024) is not { } first || (string?)first["type"] != "auth")
                {
                    await Broker.CloseAsync(socket, "auth first", ct);
                    return;
                }
                AppUser user;
                try
                {
                    var t = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync((string?)first["token"], ct);
                    user = await provisioner.ResolveOrProvisionAsync(t.Uid, t.Claims.GetValueOrDefault("email") as string, ct);
                }
                catch (Exception)
                {
                    await Broker.CloseAsync(socket, "unauthorized", ct);
                    return;
                }
                db.CurrentTenantId = user.TenantId;
                var me = new Broker.Link(socket, user.TenantId, Guid.Empty);
                await me.SendAsync(new JsonObject { ["type"] = "ready" }, ct);

                while (await Broker.ReadAsync(socket, ct) is { } msg)
                {
                    switch ((string?)msg["type"])
                    {
                        case "offer" when gateway is null:
                            // The tenant filter makes another tenant's gateway look absent:
                            // same answer as unknown/offline, so no existence oracle.
                            var ok = Guid.TryParse((string?)msg["gatewayId"], out var gatewayId)
                                && await db.Gateways.AnyAsync(d => d.Id == gatewayId, ct)
                                && Broker.Gateways.TryGetValue(gatewayId, out gateway)
                                && gateway.TenantId == user.TenantId;
                            if (!ok || gateway is null)
                            {
                                gateway = null;
                                await me.SendAsync(new JsonObject { ["type"] = "error", ["error"] = "gateway offline or unknown" }, ct);
                                continue;
                            }
                            session = Guid.NewGuid();
                            Broker.Sessions[session] = new Broker.Link(socket, user.TenantId, gatewayId);
                            await gateway.SendAsync(new JsonObject
                            {
                                ["type"] = "offer",
                                ["session"] = session.ToString(),
                                ["sdp"] = msg["sdp"]?.DeepClone(),
                            }, ct);
                            break;
                        case "ice" when gateway is not null:
                            await gateway.SendAsync(new JsonObject
                            {
                                ["type"] = "ice",
                                ["session"] = session.ToString(),
                                ["candidate"] = msg["candidate"]?.DeepClone(),
                            }, ct);
                            break;
                    }
                }
            }
            catch (Exception) { }   // a dropped browser socket must never crash the process
            finally
            {
                if (session != Guid.Empty && Broker.Sessions.TryRemove(session, out _) && gateway is not null)
                {
                    await gateway.SendAsync(new JsonObject { ["type"] = "bye", ["session"] = session.ToString() }, CancellationToken.None);
                }
            }
        });
    }
}
