using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Tests.Integration;
using Reqnroll.Microsoft.Extensions.DependencyInjection;

namespace R8er.Api.Tests.Hooks;

public static class ScenarioDeps
{
    // One fixture for the whole Reqnroll run; started once, disposed at process end.
    // Wraps the same process-global shared containers as the xUnit
    // `[Collection("integration")]` fixture (see IntegrationFixture.SharedContainers)
    // so the whole assembly uses exactly one Postgres + one Firebase emulator.
    private static readonly IntegrationFixture Shared = CreateAndStart();

    private static IntegrationFixture CreateAndStart()
    {
        var fx = new IntegrationFixture();
        fx.InitializeAsync().GetAwaiter().GetResult();
        return fx;
    }

    [ScenarioDependencies]
    public static IServiceCollection Build()
        => new ServiceCollection().AddSingleton(Shared);
}
