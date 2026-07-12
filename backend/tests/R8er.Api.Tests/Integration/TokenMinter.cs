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
        var url = $"http://{emulatorHost}/identitytoolkit.googleapis.com/v1/accounts:signUp?key=fake-api-key";
        var resp = await http.PostAsJsonAsync(url, new { email, password, returnSecureToken = true });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("idToken").GetString()!;
    }
}
