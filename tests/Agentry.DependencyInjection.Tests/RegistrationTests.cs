using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Agentry.DependencyInjection.Tests;

/// <summary>
/// Binding roles to models, and refusing to start when one is missing.
/// </summary>
public sealed class RegistrationTests
{
    private static IConfiguration Config(params (string Role, string Model)[] models) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(models.Select(m =>
                new KeyValuePair<string, string?>($"Agentry:Models:{m.Role}", m.Model)))
            .Build();

    /// <summary>Records which model name it was asked to build a client for.</summary>
    private sealed class Factory
    {
        public List<string> Built { get; } = [];

        public IChatClient Create(string model, IServiceProvider sp)
        {
            Built.Add(model);
            return new Stub();
        }

        private sealed class Stub : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
                Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }
    }

    [Fact]
    public void A_configured_role_resolves_to_a_runner()
    {
        var services = new ServiceCollection();
        var factory = new Factory();

        services.AddAgentryModels(Config(("accurate", "claude-sonnet-5")), factory.Create);

        var router = services.BuildServiceProvider().GetRequiredService<IModelRouter>();

        Assert.NotNull(router.For("accurate"));
        Assert.Equal(["claude-sonnet-5"], factory.Built);
    }

    [Fact]
    public void Clients_are_built_lazily_and_once()
    {
        var services = new ServiceCollection();
        var factory = new Factory();

        services.AddAgentryModels(Config(("accurate", "big"), ("cheap", "small")), factory.Create);
        var provider = services.BuildServiceProvider();

        // Nothing built until a role is asked for — registering ten models
        // should not open ten connections.
        Assert.Empty(factory.Built);

        var router = provider.GetRequiredService<IModelRouter>();
        router.For("accurate");
        router.For("accurate");

        // Keyed singleton: the second call reuses the first runner.
        Assert.Equal(["big"], factory.Built);
    }

    [Fact]
    public void An_unconfigured_role_names_the_ones_that_exist()
    {
        var services = new ServiceCollection();
        services.AddAgentryModels(Config(("accurate", "big")), new Factory().Create);

        var router = services.BuildServiceProvider().GetRequiredService<IModelRouter>();
        var error = Assert.Throws<ArgumentException>(() => router.For("acurate"));

        Assert.Contains("acurate", error.Message);
        Assert.Contains("accurate", error.Message);
        Assert.Contains("Agentry:Models", error.Message);
    }

    [Fact]
    public void A_role_used_in_code_but_not_configured_fails_at_registration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentryModels(Config(("cheap", "small")), new Factory().Create)
                    .ValidateRoles(["accurate", "cheap"]));

        // A startup failure, not a request that dies halfway through minutes
        // after deploy and reads as a missing service.
        Assert.Contains("accurate", error.Message);
        Assert.Contains("not configured", error.Message);
    }

    [Fact]
    public void Everything_configured_validates_quietly()
    {
        var services = new ServiceCollection();

        services.AddAgentryModels(Config(("accurate", "big"), ("cheap", "small")), new Factory().Create)
                .ValidateRoles(["accurate", "cheap"]);
    }

    [Fact]
    public void A_configured_role_nothing_asks_for_does_not_stop_startup()
    {
        var services = new ServiceCollection();

        // Dead configuration is worth noticing and not worth refusing to start
        // over — a role can legitimately outlive the code that used it by one
        // deploy.
        services.AddAgentryModels(Config(("accurate", "big"), ("stale", "old")), new Factory().Create)
                .ValidateRoles(["accurate"]);
    }

    [Fact]
    public void An_empty_section_is_not_a_crash_until_something_needs_a_role()
    {
        var services = new ServiceCollection();
        services.AddAgentryModels(Config(), new Factory().Create);

        var router = services.BuildServiceProvider().GetRequiredService<IModelRouter>();

        var error = Assert.Throws<ArgumentException>(() => router.For("accurate"));
        Assert.Contains("(none)", error.Message);
    }
}
