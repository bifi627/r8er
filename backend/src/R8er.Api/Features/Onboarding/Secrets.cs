using System.Security.Cryptography;
using System.Text;

namespace R8er.Api.Features.Onboarding;

/// Onboarding secrets (enrollment token, agent credential): random, URL-safe,
/// stored server-side as SHA-256 hex only — never the plaintext.
/// Design: docs/superpowers/specs/2026-11-01-r8er-agent-pairing-design.md (flow 1).
public static class Secrets
{
    public static string New(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
