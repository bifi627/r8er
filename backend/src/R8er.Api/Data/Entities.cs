namespace R8er.Api.Data;

public class Tenant
{
    public Guid Id { get; set; }
    public string? Name { get; set; }               // cosmetic; owner is implicit (the one user)
    public DateTimeOffset CreatedAt { get; set; }
}

public class AppUser
{
    public Guid Id { get; set; }                    // our PK — never the Firebase UID
    public string FirebaseUid { get; set; } = default!;
    public string? Email { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Device : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string? Name { get; set; }
    public string? DtlsFingerprint { get; set; }        // item 2 (pairing) fills; pinned
    public string? AgentCredentialHash { get; set; }    // item 2 fills
    public DateTimeOffset? LastSeenAt { get; set; }      // heartbeat → online/offline
    public DateTimeOffset CreatedAt { get; set; }
}
