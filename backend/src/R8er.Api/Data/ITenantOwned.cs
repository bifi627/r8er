namespace R8er.Api.Data;

/// Every tenant-owned table implements this; one global query filter
/// (R8erDbContext) scopes them all to the current request's tenant. Adding a
/// tenant-owned entity = implement this and the filter applies automatically —
/// the safe path is the default path.
public interface ITenantOwned
{
    Guid TenantId { get; }
}
