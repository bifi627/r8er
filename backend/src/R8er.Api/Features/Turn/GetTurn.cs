using System.Text.Json;

namespace R8er.Api.Features.Turn;

/// ICE servers for the client. Prod: short-lived Cloudflare TURN credentials
/// (CLOUDFLARE_TURN_KEY_ID + CLOUDFLARE_TURN_API_TOKEN), passed through as-is
/// (we trickle ICE, so the browser-blocked :53 URLs only cost a failed candidate).
/// Dev: R8ER_TURN_URLS / _USERNAME / _CREDENTIAL (compose coturn), else STUN only.
public static class GetTurn
{
    private static readonly object Stun = new { urls = new[] { "stun:stun.l.google.com:19302" } };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/turn", async (IConfiguration cfg, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (cfg["CLOUDFLARE_TURN_KEY_ID"] is { Length: > 0 } keyId)
            {
                using var rq = new HttpRequestMessage(HttpMethod.Post,
                    $"https://rtc.live.cloudflare.com/v1/turn/keys/{keyId}/credentials/generate-ice-servers")
                {
                    Content = JsonContent.Create(new { ttl = 3600 }),
                };
                rq.Headers.Authorization = new("Bearer", cfg["CLOUDFLARE_TURN_API_TOKEN"]);
                using var rs = await http.CreateClient().SendAsync(rq, ct);
                if (!rs.IsSuccessStatusCode)
                {
                    return Results.Problem($"TURN provider: HTTP {(int)rs.StatusCode}", statusCode: 502);
                }
                return Results.Ok(await rs.Content.ReadFromJsonAsync<JsonElement>(ct));
            }
            object[] servers = cfg["R8ER_TURN_URLS"] is { Length: > 0 } urls
                ? [Stun, new { urls = urls.Split(','), username = cfg["R8ER_TURN_USERNAME"], credential = cfg["R8ER_TURN_CREDENTIAL"] }]
                : [Stun];
            return Results.Ok(new { iceServers = servers });
        }).RequireAuthorization();
    }
}
