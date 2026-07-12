using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Data;
using R8er.Api.Tests.Integration;
using Reqnroll;

namespace R8er.Api.Tests.Features;

[Binding]
public class ProvisioningSteps(IntegrationFixture fx)
{
    [BeforeScenario]
    public Task Reset() => fx.ResetAsync();

    [When("a new user \"(.*)\" signs in")]
    public async Task WhenSignsIn(string email)
    {
        var token = await TokenMinter.MintIdTokenAsync(fx.EmulatorHost, email, "password123");
        using var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.GetAsync("/me")).EnsureSuccessStatusCode();
    }

    [Then("there is exactly (.*) tenant and (.*) user")]
    public async Task ThenCounts(int tenants, int users)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
        Assert.Equal(tenants, await db.Tenants.CountAsync());
        Assert.Equal(users, await db.Users.CountAsync());
    }
}
