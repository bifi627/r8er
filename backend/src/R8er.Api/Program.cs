using R8er.Api;

var builder = WebApplication.CreateBuilder(args);

// Railway (and any container host) inject the port via $PORT. ponytail:
// one-line bind; no-op locally where launchSettings sets the URL.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

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
