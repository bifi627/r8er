using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using R8er.Api.Tests.Infrastructure;
using Reqnroll;
using Hub = R8er.Api.Features.Broker.Broker;

namespace R8er.Api.Tests.Features.Broker;

[Binding]
public class BrokerSteps(IntegrationFixture fx)
{
    const string Fp = "sha-256 AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    private readonly Dictionary<string, (Guid Id, WebSocket Ws)> _gateways = new();
    // ! assigned by the browser-connect step before any step uses it
    private WebSocket _browser = null!;
    private string _session = "";

    [AfterScenario]
    public void Close()
    {
        foreach (var (_, ws) in _gateways.Values)
        {
            ws.Dispose();
        }

        _browser?.Dispose();
    }

    [Given("\"(.*)\" has an online gateway \"(.*)\"")]
    public async Task GivenOnlineGateway(string email, string name)
    {
        using var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await IdToken(email));
        var token = (await (await client.PostAsync("/gateways/enrollment-tokens", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var enrolled = await (await client.PostAsJsonAsync("/gateways/enroll", new { token, fingerprint = Fp, name }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = enrolled.GetProperty("gatewayId").GetGuid();

        var wsc = fx.Factory.Server.CreateWebSocketClient();
        wsc.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {enrolled.GetProperty("credential").GetString()}";
        var ws = await wsc.ConnectAsync(new Uri(fx.Factory.Server.BaseAddress, "/gateways/ws"), CancellationToken.None);
        while (!Hub.Gateways.ContainsKey(id))
        {
            await Task.Delay(10);   // server registers right after the upgrade
        }

        _gateways[name] = (id, ws);
    }

    [When("\"(.*)\" offers to gateway \"(.*)\"")]
    public async Task WhenOffer(string email, string name)
    {
        await BrowserAuth(await IdToken(email));
        Assert.Equal("ready", (await Receive(_browser))?.GetProperty("type").GetString());
        await Send(_browser, new { type = "offer", gatewayId = _gateways[name].Id, sdp = new { type = "offer", sdp = "v=0 fake" } });
    }

    [StepDefinition("gateway \"(.*)\" receives the offer")]
    public async Task ThenGatewayGetsOffer(string name)
    {
        var m = Assert.NotNull(await Receive(_gateways[name].Ws));
        Assert.Equal("offer", m.GetProperty("type").GetString());
        Assert.Equal("v=0 fake", m.GetProperty("sdp").GetProperty("sdp").GetString());
        var session = m.GetProperty("session").GetString();
        Assert.NotNull(session);
        _session = session;
    }

    [When("gateway \"(.*)\" answers that session")]
    public async Task WhenAnswer(string name)
    {
        await Send(_gateways[name].Ws, new { type = "answer", session = _session, sdp = new { type = "answer", sdp = "v=0 answer" } });
    }

    [Then("the browser receives the answer")]
    public async Task ThenBrowserGetsAnswer()
    {
        Assert.Equal("v=0 answer", Assert.NotNull(await Receive(_browser)).GetProperty("sdp").GetProperty("sdp").GetString());
    }

    [Then("the browser receives nothing")]
    public async Task ThenBrowserNothing()
    {
        Assert.Null(await Receive(_browser, quiet: true));
    }

    [Then("gateway \"(.*)\" receives nothing")]
    public async Task ThenGatewayNothing(string name)
    {
        Assert.Null(await Receive(_gateways[name].Ws, quiet: true));
    }

    [Then("the browser is told the gateway is offline or unknown")]
    public async Task ThenBrowserError()
    {
        Assert.Equal("error", Assert.NotNull(await Receive(_browser)).GetProperty("type").GetString());
    }

    [When("a browser authenticates with token \"(.*)\"")]
    public Task WhenBadToken(string token)
    {
        return BrowserAuth(token);
    }

    [Then("the browser socket is closed")]
    public async Task ThenClosed()
    {
        var r = await _browser.ReceiveAsync(new byte[1024], CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
    }

    private Task<string> IdToken(string email)
    {
        return TokenMinter.MintIdTokenAsync(fx.EmulatorHost, email, "password123");
    }

    private async Task BrowserAuth(string token)
    {
        _browser = await fx.Factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri(fx.Factory.Server.BaseAddress, "/broker/ws"), CancellationToken.None);
        await Send(_browser, new { type = "auth", token });
    }

    private static Task Send(WebSocket ws, object msg)
    {
        return ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg)), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // Next JSON message; with quiet, null if nothing arrives within a second.
    private static async Task<JsonElement?> Receive(WebSocket ws, bool quiet = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(quiet ? 1 : 10));
        var buf = new byte[64 * 1024];
        try
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            return r.MessageType == WebSocketMessageType.Close ? null : JsonDocument.Parse(buf.AsMemory(0, r.Count)).RootElement;
        }
        catch (OperationCanceledException) when (quiet)
        {
            return null;
        }
    }
}
