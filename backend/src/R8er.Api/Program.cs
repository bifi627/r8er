using R8er.Api;

var builder = WebApplication.CreateBuilder(args);

// ponytail: the connect harness runs from file:// (origin "null") or a static
// host and POSTs telemetry cross-origin. POC is open anyway (no auth/tenancy);
// wide-open CORS is fine here and gets scoped when telemetry graduates.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Railway (and any container host) inject the port via $PORT. ponytail:
// one-line bind; no-op locally where launchSettings sets the URL.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseWebSockets();
app.UseCors();
// Serves the connectivity harness (wwwroot/connect.html) so a phone can load it
// same-origin over Railway — file:// is desktop-only. POC step 6b.
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// POC step 6 connectivity telemetry. ponytail: one shared sink, schema ensured
// at startup; a DB outage degrades to a 503 on these two routes, never touches
// signaling (which holds no DB dependency).
var telemetry = Telemetry.Create(builder.Configuration);
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

app.Run();

// ponytail: marker so integration tests can use WebApplicationFactory<Program>
public partial class Program;
