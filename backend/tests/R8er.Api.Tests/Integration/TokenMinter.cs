using System.Net.Http.Json;
using System.Text.Json;

namespace R8er.Api.Tests.Integration;

/// Mint a real Firebase ID token via the Auth emulator's Identity Toolkit REST
/// API. The token flows through the production VerifyIdTokenAsync path — no bypass.
public static class TokenMinter
{
    public static async Task<string> MintIdTokenAsync(string emulatorHost, string email, string password)
    {
        using var http = new HttpClient();
        var payload = new { email, password, returnSecureToken = true };

        var signUpUrl = $"http://{emulatorHost}/identitytoolkit.googleapis.com/v1/accounts:signUp?key=fake-api-key";
        var signUpResp = await http.PostAsJsonAsync(signUpUrl, payload);
        if (signUpResp.IsSuccessStatusCode)
            return (await signUpResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idToken").GetString()!;

        // The emulator's users aren't reset by IntegrationFixture.ResetAsync (that
        // only truncates Postgres), so a scenario that signs the same email in more
        // than once hits an already-provisioned account. Fall back to the sign-in
        // endpoint for that case — still a real emulator round trip, still verified
        // through the production path, just the "returning user" leg instead of
        // "first ever sign-up".
        var errorBody = await signUpResp.Content.ReadFromJsonAsync<JsonElement>();
        var errorMessage = errorBody.GetProperty("error").GetProperty("message").GetString();
        if (errorMessage != "EMAIL_EXISTS")
            signUpResp.EnsureSuccessStatusCode(); // not the expected conflict — surface the real failure

        var signInUrl = $"http://{emulatorHost}/identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=fake-api-key";
        var signInResp = await http.PostAsJsonAsync(signInUrl, payload);
        signInResp.EnsureSuccessStatusCode();
        return (await signInResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idToken").GetString()!;
    }
}
