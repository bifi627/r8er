using Microsoft.EntityFrameworkCore;

namespace R8er.Api.Data;

public class R8erDbContext(DbContextOptions<R8erDbContext> options) : DbContext(options)
{
    /// Set per request by FirebaseAuthHandler after the user is resolved. Null in
    /// unauthenticated contexts ⇒ the filter matches nothing ⇒ fails closed.
    public Guid? CurrentTenantId { get; set; }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Device> Devices => Set<Device>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });

        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.FirebaseUid).IsUnique();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });

        // ONE place: apply the tenant filter to every ITenantOwned entity. A
        // strongly-typed generic lambda referencing the instance property is the
        // documented EF multi-tenancy pattern (value read per query, not baked).
        foreach (var et in b.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            typeof(R8erDbContext)
                .GetMethod(nameof(ApplyTenantFilter),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(et.ClrType)
                .Invoke(this, [b]);
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned
        => b.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
}
