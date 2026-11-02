using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using R8er.Api.Data;

namespace R8er.Api.Features.Connections;

/// Connection telemetry, start: the client reports the selected ICE candidate
/// pair once the tunnel is up. Only the caller's own gateways (global filter).
public static class StartConnection
{
    public record Request(Guid GatewayId, bool Relay, bool Ipv6, string? LocalCandidateType,
        string? RemoteCandidateType, string? Protocol);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/connections", async (Request rq, ClaimsPrincipal user, R8erDbContext db, CancellationToken ct) =>
        {
            if (!await db.Gateways.AnyAsync(g => g.Id == rq.GatewayId, ct))
            {
                return Results.NotFound();
            }
            var row = new ConnectionSession
            {
                TenantId = Guid.Parse(user.FindFirstValue("tenant_id") ?? throw new InvalidOperationException("authenticated principal without tenant_id")),
                GatewayId = rq.GatewayId,
                Relay = rq.Relay,
                Ipv6 = rq.Ipv6,
                LocalCandidateType = Clip(rq.LocalCandidateType),
                RemoteCandidateType = Clip(rq.RemoteCandidateType),
                Protocol = Clip(rq.Protocol),
            };
            db.ConnectionSessions.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { row.Id });
        }).RequireAuthorization();
    }

    // Client-supplied strings: keep them short.
    private static string? Clip(string? s)
    {
        return s is { Length: > 16 } ? s[..16] : s;
    }
}
