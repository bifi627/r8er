namespace R8er.Api.Features.Poc;

/// Phase-0 POC routes (room signaling + connectivity telemetry). Unauthenticated,
/// no tenant data; kept as-is until item 5's streaming client replaces them.
public static class PocEndpoints
{
    public static async Task MapAsync(WebApplication app, string connString)
    {
        // POC step 6 connectivity telemetry. ponytail: one shared sink, schema ensured
        // at startup; a DB outage degrades to a 503 on these two routes, never touches
        // signaling (which holds no DB dependency).
        var telemetry = Telemetry.Create(connString);
        try
        {
            await telemetry.EnsureSchemaAsync();
        }
        catch (Exception ex)
        {
            // Telemetry is best-effort POC instrumentation; a missing/unreachable DB
            // must not take signaling down with it. Log and carry on — the /telemetry
            // routes will surface their own errors when actually hit.
            app.Logger.LogWarning(ex, "telemetry: schema init failed; endpoints will error until DB is reachable");
        }

        app.MapPost("/telemetry/poc", async (TelemetryRow row, HttpContext ctx, CancellationToken ct) =>
        {
            var id = await telemetry.InsertAsync(row, ctx.Request.Headers.UserAgent.ToString(), ct);
            return Results.Ok(new { id });
        });

        app.MapGet("/telemetry/poc", (CancellationToken ct) => telemetry.ListAsync(ct: ct));

        // WebRTC signaling: relay SDP/ICE between the two peers sharing a room id.
        app.Map("/signaling/{room}", async (HttpContext ctx, string room) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await SignalingHub.RunAsync(room, socket, ctx.RequestAborted);
        });
    }
}
