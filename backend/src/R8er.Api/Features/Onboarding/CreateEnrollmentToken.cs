using System.Security.Claims;
using R8er.Api.Data;

namespace R8er.Api.Features.Onboarding;

/// "Add agent": mint a one-time enrollment token for the caller's tenant and the
/// install command that carries it.
public static class CreateEnrollmentToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/agents/enrollment-tokens", async (ClaimsPrincipal user, R8erDbContext db, HttpContext ctx, IConfiguration cfg, CancellationToken ct) =>
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
            var serverUrl = (cfg["R8ER_PUBLIC_URL"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
            // Dev form (from agent/); the docker run / Compose form arrives with item 4's image.
            var installCommand = $"R8ER_SERVER_URL={serverUrl} R8ER_ENROLL_TOKEN={token} go run ./cmd/agent";
            var installCommandPowershell =
                $"$env:R8ER_SERVER_URL='{serverUrl}'; $env:R8ER_ENROLL_TOKEN='{token}'; go run ./cmd/agent";
            return Results.Ok(new { token, expiresAt, installCommand, installCommandPowershell });
        }).RequireAuthorization();
    }
}
