using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Data;

namespace R8er.Api.Tests.Integration;

[Collection("integration")]
public class AuthAndProvisioningTests(IntegrationFixture fx)
{
    [Fact]
    public async Task No_token_is_rejected_401()
    {
        await fx.ResetAsync();
        using var client = fx.Factory.CreateClient();
        var resp = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Garbage_token_is_rejected_401()
    {
        await fx.ResetAsync();
        using var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var resp = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task First_sign_in_provisions_exactly_one_tenant_and_user()
    {
        await fx.ResetAsync();
        var token = await TokenMinter.MintIdTokenAsync(fx.EmulatorHost, "new@x.test", "password123");
        using var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/me");         // triggers provisioning
        resp.EnsureSuccessStatusCode();

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.Tenants.CountAsync());
    }

    [Fact]
    public async Task Concurrent_first_requests_provision_one_tenant_not_two()
    {
        await fx.ResetAsync();
        var token = await TokenMinter.MintIdTokenAsync(fx.EmulatorHost, "race@x.test", "password123");

        async Task Hit()
        {
            using var client = fx.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await client.GetAsync("/me")).EnsureSuccessStatusCode();
        }
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Hit()));

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.Tenants.CountAsync());
    }
}
