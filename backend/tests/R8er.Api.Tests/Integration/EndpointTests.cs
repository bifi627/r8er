using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Data;

namespace R8er.Api.Tests.Integration;

[Collection("integration")]
public class EndpointTests(IntegrationFixture fx)
{
    private async Task<HttpClient> SignedInClient(string email)
    {
        var token = await TokenMinter.MintIdTokenAsync(fx.EmulatorHost, email, "password123");
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Me_returns_the_signed_in_user_and_tenant()
    {
        await fx.ResetAsync();
        using var client = await SignedInClient("me@x.test");
        var body = await client.GetFromJsonAsync<JsonElement>("/me");
        Assert.False(string.IsNullOrEmpty(body.GetProperty("userId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("tenantId").GetString()));
        Assert.Equal("me@x.test", body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_tenant_never_sees_another_tenants_device_over_http()
    {
        await fx.ResetAsync();

        // Tenant A signs in (provisions), then gets a device seeded into its tenant.
        using var clientA = await SignedInClient("a@x.test");
        await clientA.GetAsync("/me");                       // provision A
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
            var tenantAId = db.Users.Single(u => u.Email == "a@x.test").TenantId;
            db.Devices.Add(new Device { TenantId = tenantAId, Name = "a-box" });
            await db.SaveChangesAsync();
        }

        // Tenant A sees its device; tenant B sees nothing.
        var aDevices = await clientA.GetFromJsonAsync<JsonElement>("/devices");
        Assert.Equal(1, aDevices.GetArrayLength());

        using var clientB = await SignedInClient("b@x.test");
        var bDevices = await clientB.GetFromJsonAsync<JsonElement>("/devices");
        Assert.Equal(0, bDevices.GetArrayLength());
    }
}
