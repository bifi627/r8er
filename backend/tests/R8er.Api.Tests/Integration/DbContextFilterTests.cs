using Microsoft.EntityFrameworkCore;
using R8er.Api.Data;
using Testcontainers.PostgreSql;

namespace R8er.Api.Tests.Integration;

public class DbContextFilterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    private R8erDbContext NewContext() =>
        new(new DbContextOptionsBuilder<R8erDbContext>().UseNpgsql(_pg.GetConnectionString()).Options);

    [Fact]
    public async Task Global_filter_returns_only_the_current_tenants_devices()
    {
        Guid tenantA, tenantB;
        await using (var seed = NewContext())
        {
            var a = new Tenant(); var b = new Tenant();
            seed.Tenants.AddRange(a, b);
            await seed.SaveChangesAsync();
            tenantA = a.Id; tenantB = b.Id;
            seed.Devices.Add(new Device { TenantId = tenantA, Name = "a-box" });
            seed.Devices.Add(new Device { TenantId = tenantB, Name = "b-box" });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        db.CurrentTenantId = tenantA;

        // Even a query that "forgets" a tenant Where returns only tenant A's row —
        // the filter, not the caller, enforces isolation.
        var visible = await db.Devices.ToListAsync();

        Assert.Single(visible);
        Assert.Equal("a-box", visible[0].Name);
    }

    [Fact]
    public async Task Global_filter_returns_no_rows_when_CurrentTenantId_is_null()
    {
        Guid tenantA, tenantB;
        await using (var seed = NewContext())
        {
            var a = new Tenant(); var b = new Tenant();
            seed.Tenants.AddRange(a, b);
            await seed.SaveChangesAsync();
            tenantA = a.Id; tenantB = b.Id;
            seed.Devices.Add(new Device { TenantId = tenantA, Name = "a-box" });
            seed.Devices.Add(new Device { TenantId = tenantB, Name = "b-box" });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        db.CurrentTenantId = null;

        // Pre-auth state: no tenant means no rows, ever — fails closed, not open.
        var visible = await db.Devices.ToListAsync();

        Assert.Empty(visible);
    }
}
