using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StreamIq.Api.Data;

/// dotnet-ef needs to build a DbContext without the app's DI container (which
/// only registers StreamIqDbContext once request wiring lands — a later task).
/// Design-time only: `migrations add` never opens a connection, so the
/// connection string just has to parse, not resolve. Same local-compose
/// default as Program.cs's fallback.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StreamIqDbContext>
{
    public StreamIqDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StreamIqDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Username=streamiq;Password=dev;Database=streamiq")
            .Options;
        return new StreamIqDbContext(options);
    }
}
