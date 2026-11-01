using System.Reflection;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using R8er.Api;
using R8er.Api.Auth;
using R8er.Api.Data;
using R8er.Api.Features.Devices;
using R8er.Api.Features.Me;
using R8er.Api.Features.Onboarding;
using R8er.Api.Features.Poc;

var builder = WebApplication.CreateBuilder(args);

// ponytail: the connect harness runs from file:// (origin "null") or a static
// host and POSTs telemetry cross-origin. POC is open anyway (no auth/tenancy);
// wide-open CORS is fine here and gets scoped when telemetry graduates.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// EF Core control-plane store. Key mirrors .env.example (ConnectionStrings__Default);
// falls back to the local compose Postgres. POC Telemetry.cs shares this string.
builder.Configuration.AddUserSecrets<Program>();
// Railway's Postgres hands out DATABASE_URL in URL form (postgres://u:p@host:port/db),
// which Npgsql can't parse. Accept either a native key-value string OR a URL from any
// of the usual keys, converting the URL case. ponytail: covers ConnectionStrings__Default
// and Railway's DATABASE_URL with no hand-assembled connection string to get wrong.
var rawConn = builder.Configuration.GetConnectionString("Default")
    ?? builder.Configuration["DATABASE_URL"]
    ?? "Host=localhost;Port=5432;Database=r8er;Username=r8er;Password=dev";
var connString = R8er.Api.ConnectionString.Normalize(rawConn);
builder.Services.AddDbContext<R8erDbContext>(o => o.UseNpgsql(connString));

// FirebaseAdmin init. Emulator (dev): FIREBASE_AUTH_EMULATOR_HOST is set and
// VerifyIdTokenAsync skips the signature — same call as prod. A dummy credential
// satisfies init in emulator mode. Prod needs a real service account: Railway has
// no ambient ADC, so we read the JSON from FIREBASE_SERVICE_ACCOUNT_JSON and fall
// back to GetApplicationDefault() only for GCP-hosted runs.
var firebaseProjectId = builder.Configuration["FIREBASE_PROJECT_ID"] ?? "demo-r8er";
if (FirebaseApp.DefaultInstance is null)
{
    var usingEmulator = !string.IsNullOrEmpty(builder.Configuration["FIREBASE_AUTH_EMULATOR_HOST"]);
    // ponytail: read the env var directly, not via Configuration — a multi-line/JSON
    // value plus provider ordering (UserSecrets appended at L22) can shadow it. Direct
    // read is the source of truth for a container-injected secret.
    var serviceAccountJson = (Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT_JSON")
        ?? builder.Configuration["FIREBASE_SERVICE_ACCOUNT_JSON"])?.Trim();
    if (!usingEmulator && string.IsNullOrEmpty(serviceAccountJson))
        throw new InvalidOperationException(
            "FIREBASE_SERVICE_ACCOUNT_JSON is empty at runtime (and no emulator host set). " +
            "The process cannot see the env var — check it's set on the running service, not just build.");
    // Typed parse rejects non-service-account JSON (why GoogleCredential.FromJson was deprecated).
    FirebaseApp.Create(new AppOptions
    {
        ProjectId = firebaseProjectId,
        Credential = usingEmulator
            ? GoogleCredential.FromAccessToken("owner")     // dummy; emulator ignores it
            : CredentialFactory.FromJson<ServiceAccountCredential>(serviceAccountJson).ToGoogleCredential(),
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

// Vertical slices: one file per use case under Features/<Slice>/, mapped here.
await PocEndpoints.MapAsync(app, connString);
GetMe.Map(app);
ListDevices.Map(app);
CreateEnrollmentToken.Map(app);
Enroll.Map(app);
AgentSocket.Map(app);

app.Run();

// ponytail: marker so integration tests can use WebApplicationFactory<Program>
public partial class Program;
