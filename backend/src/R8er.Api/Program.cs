using System.Reflection;
using System.Security.Claims;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using R8er.Api;
using R8er.Api.Auth;
using R8er.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// ponytail: the connect harness runs from file:// (origin "null") or a static
// host and POSTs telemetry cross-origin. POC is open anyway (no auth/tenancy);
// wide-open CORS is fine here and gets scoped when telemetry graduates.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// EF Core control-plane store. Key mirrors .env.example (ConnectionStrings__Default);
// falls back to the local compose Postgres. (POC Telemetry.cs keeps its own raw
// Npgsql connection — untouched here; it graduates in item 3.)
builder.Configuration.AddUserSecrets<Program>();
var connString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=r8er;Username=r8er;Password=dev";
builder.Services.AddDbContext<R8erDbContext>(o => o.UseNpgsql(connString));

// FirebaseAdmin init. Emulator (dev): FIREBASE_AUTH_EMULATOR_HOST is set and
// VerifyIdTokenAsync skips the signature — same call as prod. A dummy credential
// satisfies init in emulator mode; prod uses application-default credentials.
var firebaseProjectId = builder.Configuration["FIREBASE_PROJECT_ID"] ?? "demo-r8er";
if (FirebaseApp.DefaultInstance is null)
{
    var usingEmulator = !string.IsNullOrEmpty(builder.Configuration["FIREBASE_AUTH_EMULATOR_HOST"]);
    FirebaseApp.Create(new AppOptions
    {
        ProjectId = firebaseProjectId,
        Credential = usingEmulator
            ? GoogleCredential.FromAccessToken("owner")     // dummy; emulator ignores it
            : GoogleCredential.GetApplicationDefault(),
    });
}

builder.Services.AddScoped<UserProvisioner>();
builder.Services.AddAuthentication("Firebase")
    .AddScheme<AuthenticationSchemeOptions, FirebaseAuthHandler>("Firebase", _ => { });
builder.Services.AddAuthorization();

// Railway (and any container host) inject the port via $PORT. ponytail:
// one-line bind; no-op locally where launchSettings sets the URL.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// ponytail: single-instance MVP — migrate on boot. Move to a deploy/CI step if we
// ever run multiple API instances (concurrent migrate is unsafe).
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<R8erDbContext>().Database.MigrateAsync();

app.UseAuthentication();
app.UseAuthorization();

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

// Signed-in user + their tenant. First call for a new Firebase UID provisions
// tenant+user in the auth handler.
app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    userId = user.FindFirstValue(ClaimTypes.NameIdentifier),
    email = user.FindFirstValue(ClaimTypes.Email),
    tenantId = user.FindFirstValue("tenant_id"),
})).RequireAuthorization();

// The caller's devices — auto-scoped by the global tenant filter (empty until
// pairing lands in item 2). No hand-written tenant Where: the filter enforces it.
app.MapGet("/devices", async (R8erDbContext db, CancellationToken ct) =>
    await db.Devices.OrderBy(d => d.CreatedAt)
        .Select(d => new { d.Id, d.Name, d.LastSeenAt })
        .ToListAsync(ct))
    .RequireAuthorization();

app.Run();

// ponytail: marker so integration tests can use WebApplicationFactory<Program>
public partial class Program;
