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
    public string? DtlsFingerprint { get; set; }        // agent's persistent DTLS cert, set at enrollment; pinned
    public string? AgentCredentialHash { get; set; }    // SHA-256 hex of the agent credential (never the plaintext)
    public string? LanUrl { get; set; }                 // agent page on the home LAN, from heartbeat
    public DateTimeOffset? LastSeenAt { get; set; }      // heartbeat → online/offline
    public DateTimeOffset CreatedAt { get; set; }
}

/// One-time agent enrollment token (onboarding flow 1). Only the SHA-256 hash
/// is stored; the plaintext lives in the install command for 15 minutes.
public class EnrollmentToken : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string TokenHash { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
