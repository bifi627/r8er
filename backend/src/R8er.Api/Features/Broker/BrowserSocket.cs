using System.Text.Json.Nodes;
using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using R8er.Api.Auth;
using R8er.Api.Data;

namespace R8er.Api.Features.Broker;

/// A browser's signaling socket: one WebSocket = one session to one agent. The
/// Firebase ID token arrives as the first message (not a query param — keeps it
/// out of logs); the offer may only target an online agent of the caller's tenant.
public static class BrowserSocket
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map("/broker/ws", async (HttpContext ctx, R8erDbContext db, UserProvisioner provisioner) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var ct = ctx.RequestAborted;
            Guid session = Guid.Empty;
            Broker.Link? agent = null;
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
                        case "offer" when agent is null:
                            // The tenant filter makes another tenant's device look absent:
                            // same answer as unknown/offline, so no existence oracle.
                            var ok = Guid.TryParse((string?)msg["deviceId"], out var deviceId)
                                && await db.Devices.AnyAsync(d => d.Id == deviceId, ct)
                                && Broker.Agents.TryGetValue(deviceId, out agent)
                                && agent.TenantId == user.TenantId;
                            if (!ok || agent is null)
                            {
                                agent = null;
                                await me.SendAsync(new JsonObject { ["type"] = "error", ["error"] = "agent offline or unknown" }, ct);
                                continue;
                            }
                            session = Guid.NewGuid();
                            Broker.Sessions[session] = new Broker.Link(socket, user.TenantId, deviceId);
                            await agent.SendAsync(new JsonObject
                            {
                                ["type"] = "offer",
                                ["session"] = session.ToString(),
                                ["sdp"] = msg["sdp"]?.DeepClone(),
                            }, ct);
                            break;
                        case "ice" when agent is not null:
                            await agent.SendAsync(new JsonObject
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
                if (session != Guid.Empty && Broker.Sessions.TryRemove(session, out _) && agent is not null)
                {
                    await agent.SendAsync(new JsonObject { ["type"] = "bye", ["session"] = session.ToString() }, CancellationToken.None);
                }
            }
        });
    }
}
