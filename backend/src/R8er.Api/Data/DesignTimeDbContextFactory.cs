using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace R8er.Api.Data;

/// dotnet-ef needs to build a DbContext without the app's DI container (which
/// only registers R8erDbContext once request wiring lands — a later task).
/// Design-time only: `migrations add` never opens a connection, so the
/// connection string just has to parse, not resolve. Same local-compose
/// default as Telemetry.Create's fallback.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<R8erDbContext>
{
    public R8erDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<R8erDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Username=r8er;Password=dev;Database=r8er")
            .Options;
        return new R8erDbContext(options);
    }
}
