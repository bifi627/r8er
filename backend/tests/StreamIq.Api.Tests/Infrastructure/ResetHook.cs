using Reqnroll;

namespace StreamIq.Api.Tests.Infrastructure;

// The one clean-DB reset for every Reqnroll scenario. Untagged hooks are global,
// so step classes must not add their own (each extra copy = another TRUNCATE).
[Binding]
public class ResetHook(IntegrationFixture fx)
{
    [BeforeScenario]
    public Task Reset()
    {
        return fx.ResetAsync();
    }
}
