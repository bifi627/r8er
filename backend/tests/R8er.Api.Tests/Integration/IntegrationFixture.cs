using System.IO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Data;
using Testcontainers.PostgreSql;

namespace R8er.Api.Tests.Integration;

/// One Postgres + one Firebase Auth emulator shared across the whole integration
/// suite. Single emulator ⇒ the process-global FIREBASE_AUTH_EMULATOR_HOST is set
/// once (see R8erApiFactory). Reused image + command from docker-compose.yml.
/// ponytail: npx pulls firebase-tools at start; if CI startup is too slow, bake a
/// node+firebase-tools Dockerfile and .WithImage(ImageFromDockerfile).
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17").Build();

    private readonly IContainer _firebase = new ContainerBuilder("node:22-alpine")
        .WithResourceMapping(
            new FileInfo(Path.Combine(RepoRoot.Find(), "dev", "firebase", "firebase.json")),
            "/app/")
        .WithWorkingDirectory("/app")
        .WithCommand("npx", "-y", "firebase-tools", "emulators:start", "--only", "auth", "--project", "demo-r8er")
        .WithPortBinding(9099, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("All emulators ready"))
        .Build();

    public R8erApiFactory Factory { get; private set; } = default!;
    public string EmulatorHost { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _firebase.StartAsync());
        EmulatorHost = $"{_firebase.Hostname}:{_firebase.GetMappedPublicPort(9099)}";
        Factory = new R8erApiFactory(_pg.GetConnectionString(), EmulatorHost);

        // Force host build + apply migrations before any test runs.
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<R8erDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Task.WhenAll(_pg.DisposeAsync().AsTask(), _firebase.DisposeAsync().AsTask());
    }

    /// Per-test reset: truncate all tenant data (lazy, no Respawn dependency).
    public async Task ResetAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE devices, users, tenants RESTART IDENTITY CASCADE;");
    }
}

[CollectionDefinition("integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
