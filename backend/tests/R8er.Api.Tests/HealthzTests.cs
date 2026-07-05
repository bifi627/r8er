using Microsoft.AspNetCore.Mvc.Testing;

namespace R8er.Api.Tests;

public class HealthzTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_returns_success()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.EnsureSuccessStatusCode();
    }
}
