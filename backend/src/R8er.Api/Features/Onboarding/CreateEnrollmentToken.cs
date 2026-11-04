using System.Security.Claims;
using R8er.Api.Data;

namespace R8er.Api.Features.Onboarding;

/// "Add gateway": mint a one-time enrollment token for the caller's tenant and the
/// install command that carries it.
public static class CreateEnrollmentToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/gateways/enrollment-tokens", async (ClaimsPrincipal user, R8erDbContext db, HttpContext ctx, IConfiguration cfg, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var token = Secrets.New(16);   // 128 bit
            var expiresAt = DateTimeOffset.UtcNow + Lifetime;
            db.EnrollmentTokens.Add(new EnrollmentToken
            {
                TenantId = Guid.Parse(user.FindFirstValue("tenant_id") ?? throw new InvalidOperationException("authenticated principal without tenant_id")),
                TokenHash = Secrets.Hash(token),
                ExpiresAt = expiresAt,
            });
            await db.SaveChangesAsync(ct);

            // ponytail: R8ER_PUBLIC_URL when set (prod, behind Railway's proxy), else the
            // URL this request came in on (dev: http://localhost:5028).
            var apiUrl = (cfg["R8ER_PUBLIC_URL"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
            // Development: run from source in gateway/. Elsewhere: the released binary
            // via the install script (GitHub Releases; it also updates later).
            const string dl = "https://github.com/bifi627/r8er/releases/latest/download";
            var vars = $"R8ER_API_URL={apiUrl} R8ER_ENROLL_TOKEN={token}";
            var psVars = $"$env:R8ER_API_URL='{apiUrl}'; $env:R8ER_ENROLL_TOKEN='{token}';";
            var (installCommand, installCommandPowershell) = env.IsDevelopment()
                ? ($"{vars} go run ./cmd/gateway", $"{psVars} go run ./cmd/gateway")
                : ($"curl -fsSL {dl}/install.sh | {vars} sh", $"{psVars} irm {dl}/install.ps1 | iex");
            return Results.Ok(new { token, expiresAt, installCommand, installCommandPowershell });
        }).RequireAuthorization();
    }
}
