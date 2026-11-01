using System.Security.Claims;

namespace R8er.Api.Features.Me;

/// Signed-in user + their tenant. First call for a new Firebase UID provisions
/// tenant+user in the auth handler.
public static class GetMe
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            userId = user.FindFirstValue(ClaimTypes.NameIdentifier),
            email = user.FindFirstValue(ClaimTypes.Email),
            tenantId = user.FindFirstValue("tenant_id"),
        })).RequireAuthorization();
}
