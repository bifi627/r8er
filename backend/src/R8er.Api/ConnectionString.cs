using Npgsql;

namespace R8er.Api;

// ponytail: exists only to turn Railway's URL-form DATABASE_URL into an Npgsql
// key-value string. If a host ever emits some other URL scheme, widen the check.
public static class ConnectionString
{
    // Npgsql accepts "Host=...;Port=...;" but not "postgres://u:p@host:port/db".
    // Convert the URL form; pass a key-value string through untouched.
    public static string Normalize(string raw)
    {
        if (!raw.StartsWith("postgres://") && !raw.StartsWith("postgresql://"))
            return raw;

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        // Railway's public proxy needs TLS; sslmode in the URL query wins if present.
        if (uri.Query.Contains("sslmode=", StringComparison.OrdinalIgnoreCase))
            b.SslMode = uri.Query.Contains("require", StringComparison.OrdinalIgnoreCase)
                ? SslMode.Require : SslMode.Prefer;
        return b.ConnectionString;
    }
}
