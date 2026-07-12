using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Data;
using R8er.Api.Tests.Integration;
using Reqnroll;

namespace R8er.Api.Tests.Features;

[Binding]
public class TenantIsolationSteps(IntegrationFixture fx)
{
    private JsonElement _lastList;

    [BeforeScenario]
    public Task Reset() => fx.ResetAsync();

    private async Task<HttpClient> SignIn(string email)
    {
        var token = await TokenMinter.MintIdTokenAsync(fx.EmulatorHost, email, "password123");
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await client.GetAsync("/me");     // ensure provisioned
        return client;
    }

    [Given("a signed-in user \"(.*)\"")]
    public async Task GivenSignedIn(string email) => (await SignIn(email)).Dispose();

    [Given("that user's tenant owns a device named \"(.*)\"")]
    public async Task GivenOwnsDevice(string name)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
        var tenantId = db.Users.OrderBy(u => u.CreatedAt).Last().TenantId;
        db.Devices.Add(new Device { TenantId = tenantId, Name = name });
        await db.SaveChangesAsync();
    }

    [When("\"(.*)\" signs in and lists devices")]
    [When("\"(.*)\" lists devices")]
    public async Task WhenLists(string email)
    {
        using var client = await SignIn(email);
        _lastList = await client.GetFromJsonAsync<JsonElement>("/devices");
    }

    [Then("the device list is empty")]
    public void ThenEmpty() => Assert.Equal(0, _lastList.GetArrayLength());

    [Then("the device list contains \"(.*)\"")]
    public void ThenContains(string name)
    {
        var names = _lastList.EnumerateArray().Select(d => d.GetProperty("name").GetString());
        Assert.Contains(name, names);
    }
}
