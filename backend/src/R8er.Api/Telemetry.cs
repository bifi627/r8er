using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace R8er.Api;

/// POC connectivity telemetry (implementation-plan Phase 0, step 6). Every
/// WebRTC session the connect harness runs POSTs one row here: was it direct or
/// relayed, over IPv4 or IPv6, which candidate pair won, and how fast. The
/// Checkpoint-1 verdict ("direct-connect rate for cellular-to-home") is a query
/// over this table. ponytail: raw Npgsql, no EF/migrations — this is a throwaway
/// spike sink; MVP promotes it into a real tenant-scoped EF entity.
public sealed class Telemetry
{
    private readonly NpgsqlDataSource _db;

    private Telemetry(NpgsqlDataSource db) => _db = db;

    /// Build from config. Accepts either a keyword connection string
    /// (ConnectionStrings:Postgres) or a URL (DATABASE_URL, the form Railway's
    /// Postgres plugin injects); falls back to the local compose Postgres.
    public static Telemetry Create(IConfiguration cfg)
    {
        var conn =
            ToKeywordString(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? cfg.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Username=r8er;Password=dev;Database=r8er";
        return new Telemetry(NpgsqlDataSource.Create(conn));
    }

    // Railway/Heroku-style "postgresql://user:pass@host:port/db" -> Npgsql keywords.
    private static string? ToKeywordString(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)) return url;
        var u = new Uri(url);
        var creds = u.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = u.Host,
            Port = u.Port > 0 ? u.Port : 5432,
            Username = Uri.UnescapeDataString(creds[0]),
            Password = creds.Length > 1 ? Uri.UnescapeDataString(creds[1]) : "",
            Database = u.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Prefer, // Railway Postgres wants TLS; local ignores it
        };
        return b.ConnectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS poc_connectivity (
                id                     bigserial PRIMARY KEY,
                created_at             timestamptz NOT NULL DEFAULT now(),
                note                   text,
                ice_policy             text,
                connection_type        text,
                local_candidate_type   text,
                remote_candidate_type  text,
                protocol               text,
                local_ip_family        text,
                remote_ip_family       text,
                throughput_mbps        double precision,
                rtt_ms                 double precision,
                user_agent             text,
                raw_stats              jsonb
            );
            """;
        await using var cmd = _db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertAsync(TelemetryRow row, string? userAgent, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO poc_connectivity
              (note, ice_policy, connection_type, local_candidate_type,
               remote_candidate_type, protocol, local_ip_family, remote_ip_family,
               throughput_mbps, rtt_ms, user_agent, raw_stats)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            RETURNING id;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.Add(Text(row.Note));
        cmd.Parameters.Add(Text(row.IcePolicy));
        cmd.Parameters.Add(Text(row.ConnectionType));
        cmd.Parameters.Add(Text(row.LocalCandidateType));
        cmd.Parameters.Add(Text(row.RemoteCandidateType));
        cmd.Parameters.Add(Text(row.Protocol));
        cmd.Parameters.Add(Text(row.LocalIpFamily));
        cmd.Parameters.Add(Text(row.RemoteIpFamily));
        cmd.Parameters.Add(Dbl(row.ThroughputMbps));
        cmd.Parameters.Add(Dbl(row.RttMs));
        cmd.Parameters.Add(Text(userAgent));
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = row.RawStats is { } r ? r.GetRawText() : (object)DBNull.Value,
        });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// Recent rows + a direct-vs-relay summary — the number the checkpoint reads.
    public async Task<TelemetryList> ListAsync(int limit = 200, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, created_at, note, ice_policy, connection_type,
                   local_candidate_type, remote_candidate_type, protocol,
                   local_ip_family, remote_ip_family, throughput_mbps, rtt_ms
            FROM poc_connectivity
            ORDER BY created_at DESC
            LIMIT $1;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(limit);
        var rows = new List<TelemetryListRow>();
        int direct = 0, relay = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = Str(reader, 4);
            if (type == "direct") direct++;
            else if (type == "relay") relay++;
            rows.Add(new TelemetryListRow(
                reader.GetInt64(0), reader.GetDateTime(1), Str(reader, 2), Str(reader, 3),
                type, Str(reader, 5), Str(reader, 6), Str(reader, 7), Str(reader, 8), Str(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetDouble(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11)));
        }
        return new TelemetryList(new TelemetrySummary(direct + relay, direct, relay), rows);
    }

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Text(string? v) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)v ?? DBNull.Value };

    private static NpgsqlParameter Dbl(double? v) =>
        new() { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)v ?? DBNull.Value };
}

/// The JSON body the connect harness POSTs. camelCase over the wire; the raw
/// stats blob is the selected candidate pair + its two candidates, kept for
/// forensic re-reading without reshaping the table.
public sealed record TelemetryRow
{
    public string? Note { get; init; }
    public string? IcePolicy { get; init; }
    public string? ConnectionType { get; init; }
    public string? LocalCandidateType { get; init; }
    public string? RemoteCandidateType { get; init; }
    public string? Protocol { get; init; }
    public string? LocalIpFamily { get; init; }
    public string? RemoteIpFamily { get; init; }
    public double? ThroughputMbps { get; init; }
    public double? RttMs { get; init; }
    public JsonElement? RawStats { get; init; }
}

public sealed record TelemetryList(TelemetrySummary Summary, IReadOnlyList<TelemetryListRow> Rows);

public sealed record TelemetrySummary(int Total, int Direct, int Relay);

public sealed record TelemetryListRow(
    long Id, DateTime CreatedAt, string? Note, string? IcePolicy, string? ConnectionType,
    string? LocalCandidateType, string? RemoteCandidateType, string? Protocol,
    string? LocalIpFamily, string? RemoteIpFamily, double? ThroughputMbps, double? RttMs);
