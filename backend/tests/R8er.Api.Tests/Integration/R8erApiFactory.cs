using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace R8er.Api.Tests.Integration;

/// Boots the real API against the test containers. FIREBASE_AUTH_EMULATOR_HOST is
/// process-global and read by FirebaseAdmin at FirebaseApp.Create — set it before
/// the host builds. Config keys mirror .env.example.
public sealed class R8erApiFactory(string connString, string emulatorHost)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("FIREBASE_AUTH_EMULATOR_HOST", emulatorHost);
        builder.UseSetting("ConnectionStrings:Default", connString);
        builder.UseSetting("FIREBASE_PROJECT_ID", "demo-r8er");
    }
}
