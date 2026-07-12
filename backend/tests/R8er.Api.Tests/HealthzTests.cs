using R8er.Api.Tests.Integration;

namespace R8er.Api.Tests;

// Boots through the shared IntegrationFixture: Program's startup now hard-requires
// Firebase init + a DB migrate (Task 3), so the app can only boot against the
// configured factory (emulator host + Testcontainers Postgres). /healthz is
// anonymous, so it still returns 200 through that fully-wired host.
[Collection("integration")]
public class HealthzTests(IntegrationFixture fx)
{
    [Fact]
    public async Task Healthz_returns_success()
    {
        using var client = fx.Factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.EnsureSuccessStatusCode();
    }
}
