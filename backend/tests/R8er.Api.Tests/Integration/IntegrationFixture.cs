using System.IO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R8er.Api.Data;
using Testcontainers.PostgreSql;

namespace R8er.Api.Tests.Integration;

/// One Postgres + one Firebase Auth emulator shared across the WHOLE test
/// assembly — both the xUnit `[Collection("integration")]` tests and Reqnroll's
/// `[ScenarioDependencies]` (see Hooks/ScenarioDeps.cs) construct their own
/// `IntegrationFixture` instance, but every instance wraps the same lazily-started
/// process-global `SharedContainers`. That keeps FIREBASE_AUTH_EMULATOR_HOST at one
/// stable value for the whole run — a second emulator would fight the first over
/// that process-global env var and real tokens minted from one would fail
/// verification when it points at the other. Reused image + command from
/// docker-compose.yml.
/// ponytail: npx pulls firebase-tools at start; if CI startup is too slow, bake a
/// node+firebase-tools Dockerfile and .WithImage(ImageFromDockerfile).
public sealed class IntegrationFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<SharedContainers>> Shared = new(SharedContainers.StartAsync);

    private SharedContainers _shared = default!;

    public R8erApiFactory Factory => _shared.Factory;
    public string EmulatorHost => _shared.EmulatorHost;

    public async Task InitializeAsync() => _shared = await Shared.Value;

    // No-op: the shared containers live for the whole process (started at most
    // once via the Lazy above) since more than one owner (xUnit's collection
    // fixture, Reqnroll's ScenarioDeps) holds an IntegrationFixture pointing at
    // them. Testcontainers' Ryuk sidecar reaps the containers when the process
    // exits, so nothing leaks.
    public Task DisposeAsync() => Task.CompletedTask;

    /// Per-test reset: truncate all tenant data (lazy, no Respawn dependency).
    public Task ResetAsync() => _shared.ResetAsync();

    private sealed class SharedContainers(PostgreSqlContainer pg, IContainer firebase, R8erApiFactory factory, string emulatorHost)
    {
        // Rooted for the life of the process (never disposed — see IntegrationFixture.DisposeAsync)
        // so the underlying Docker containers stay up until Ryuk reaps them at process exit.
        private readonly PostgreSqlContainer _pg = pg;
        private readonly IContainer _firebase = firebase;

        public R8erApiFactory Factory { get; } = factory;
        public string EmulatorHost { get; } = emulatorHost;

        public static async Task<SharedContainers> StartAsync()
        {
            var pg = new PostgreSqlBuilder("postgres:17").Build();
            var firebase = new ContainerBuilder("node:22-alpine")
                .WithResourceMapping(
                    new FileInfo(Path.Combine(RepoRoot.Find(), "dev", "firebase", "firebase.json")),
                    "/app/")
                .WithWorkingDirectory("/app")
                .WithCommand("npx", "-y", "firebase-tools", "emulators:start", "--only", "auth", "--project", "demo-r8er")
                .WithPortBinding(9099, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("All emulators ready"))
                .Build();

            await Task.WhenAll(pg.StartAsync(), firebase.StartAsync());
            var emulatorHost = $"{firebase.Hostname}:{firebase.GetMappedPublicPort(9099)}";
            var factory = new R8erApiFactory(pg.GetConnectionString(), emulatorHost);

            // Force host build + apply migrations before any test runs.
            using (var scope = factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<R8erDbContext>().Database.MigrateAsync();
            }

            return new SharedContainers(pg, firebase, factory, emulatorHost);
        }

        public async Task ResetAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<R8erDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE devices, users, tenants RESTART IDENTITY CASCADE;");
        }
    }
}

[CollectionDefinition("integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
