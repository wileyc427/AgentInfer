using Xunit;

namespace Agentry.Tests;

public sealed class ModelRouterTests
{
    private static AgentRunner Runner() => new(new FakeChatClient("ok"));

    [Fact]
    public void A_configured_role_resolves()
    {
        var runner = Runner();
        var router = new ModelRouter([new KeyValuePair<string, AgentRunner>("accurate", runner)]);

        Assert.Same(runner, router.For("accurate"));
    }

    [Fact]
    public void An_unconfigured_role_says_which_ones_exist()
    {
        var router = new ModelRouter([new KeyValuePair<string, AgentRunner>("accurate", Runner())]);

        var error = Assert.Throws<ArgumentException>(() => router.For("acurate"));

        // A typo is otherwise indistinguishable from a missing registration,
        // and both present as a null reference somewhere else.
        Assert.Contains("acurate", error.Message);
        Assert.Contains("accurate", error.Message);
    }

    [Fact]
    public void Roles_are_case_sensitive()
    {
        var router = new ModelRouter([new KeyValuePair<string, AgentRunner>("accurate", Runner())]);

        // Ordinal, like every other identifier in this library. Matching
        // case-insensitively would make "Accurate" work here and fail against a
        // keyed-DI router, which is worse than failing consistently.
        Assert.Throws<ArgumentException>(() => router.For("Accurate"));
    }

    [Fact]
    public void A_delegate_router_defers_to_its_function()
    {
        var runner = Runner();
        var router = new DelegateModelRouter(role => role == "cheap" ? runner : throw new ArgumentException(role));

        Assert.Same(runner, router.For("cheap"));
        Assert.Throws<ArgumentException>(() => router.For("other"));
    }
}
