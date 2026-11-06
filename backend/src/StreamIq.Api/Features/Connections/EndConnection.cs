using Microsoft.EntityFrameworkCore;
using StreamIq.Api.Data;

namespace StreamIq.Api.Features.Connections;

/// Connection telemetry, end: byte totals; duration = EndedAt - CreatedAt.
/// Another tenant's row is invisible to the global filter → 404.
public static class EndConnection
{
    public record Request(long BytesReceived, long BytesSent);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPatch("/connections/{id:guid}", async (Guid id, Request rq, StreamIqDbContext db, CancellationToken ct) =>
        {
            var row = await db.ConnectionSessions.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            row.BytesReceived = rq.BytesReceived;
            row.BytesSent = rq.BytesSent;
            row.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
