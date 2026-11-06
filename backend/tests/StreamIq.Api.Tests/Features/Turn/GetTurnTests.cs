using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using StreamIq.Api.Tests.Infrastructure;

namespace StreamIq.Api.Tests.Features.Turn;

[Collection("integration")]
public class GetTurnTests(IntegrationFixture fx)
{
    [Fact]
    public async Task Turn_requires_auth()
    {
        using var client = fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/turn")).StatusCode);
    }

    [Fact]
    public async Task Without_TURN_config_it_hands_out_STUN_only()
    {
        var token = await TokenMinter.MintIdTokenAsync(fx.EmulatorHost, "turn@x.test", "password123");
        using var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var servers = (await client.GetFromJsonAsync<JsonElement>("/turn")).GetProperty("iceServers");
        Assert.Equal(1, servers.GetArrayLength());
        Assert.StartsWith("stun:", servers[0].GetProperty("urls")[0].GetString());
    }
}
