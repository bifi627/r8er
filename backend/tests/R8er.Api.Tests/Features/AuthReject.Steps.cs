using System.Net.Http.Headers;
using R8er.Api.Tests.Integration;
using Reqnroll;

namespace R8er.Api.Tests.Features;

[Binding]
public class AuthRejectSteps(IntegrationFixture fx)
{
    private int _status;

    [When("an anonymous request hits \"(.*)\"")]
    public async Task WhenAnon(string path)
    {
        using var client = fx.Factory.CreateClient();
        _status = (int)(await client.GetAsync(path)).StatusCode;
    }

    [When("a request with token \"(.*)\" hits \"(.*)\"")]
    public async Task WhenToken(string token, string path)
    {
        using var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _status = (int)(await client.GetAsync(path)).StatusCode;
    }

    [Then("the response status is (.*)")]
    public void ThenStatus(int expected) => Assert.Equal(expected, _status);
}
