using Microsoft.EntityFrameworkCore;
using R8er.Api.Data;

namespace R8er.Api.Features.Devices;

/// The caller's devices — auto-scoped by the global tenant filter. No hand-written
/// tenant Where: the filter enforces it.
public static class ListDevices
{
    /// 3 missed 30s agent heartbeats ⇒ offline.
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/devices", async (R8erDbContext db, CancellationToken ct) =>
        {
            var onlineSince = DateTimeOffset.UtcNow - OnlineWindow;
            return await db.Devices.OrderBy(d => d.CreatedAt)
                .Select(d => new { d.Id, d.Name, d.LastSeenAt, d.LanUrl, Online = d.LastSeenAt > onlineSince })
                .ToListAsync(ct);
        }).RequireAuthorization();
}
