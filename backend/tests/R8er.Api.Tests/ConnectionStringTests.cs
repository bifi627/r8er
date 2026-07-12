using R8er.Api;

namespace R8er.Api.Tests;

// The one thing that must not break: Railway's URL-form DATABASE_URL becomes a
// connection string Npgsql accepts, and an already-key-value string is untouched.
public class ConnectionStringTests
{
    [Fact]
    public void Converts_railway_url_to_npgsql_keyvalue()
    {
        var url = "postgresql://r8er:s3cret@monorail.proxy.rlwy.net:5432/railway";
        var cs = ConnectionString.Normalize(url);

        Assert.Contains("Host=monorail.proxy.rlwy.net", cs);
        Assert.Contains("Port=5432", cs);
        Assert.Contains("Database=railway", cs);
        Assert.Contains("Username=r8er", cs);
        Assert.Contains("Password=s3cret", cs);
        Assert.DoesNotContain("postgresql://", cs);
    }

    [Fact]
    public void Passes_keyvalue_string_through_untouched()
    {
        var kv = "Host=localhost;Port=5432;Database=r8er;Username=r8er;Password=dev";
        Assert.Equal(kv, ConnectionString.Normalize(kv));
    }

    [Fact]
    public void Unescapes_special_chars_in_password()
    {
        var url = "postgres://u:p%40ss%3Aword@host:6543/db";
        var cs = ConnectionString.Normalize(url);
        Assert.Contains("Password=p@ss:word", cs);
    }
}
