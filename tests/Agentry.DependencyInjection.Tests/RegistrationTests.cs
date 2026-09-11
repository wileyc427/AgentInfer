using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Agentry.DependencyInjection.Tests;

/// <summary>
/// Binding roles to models across providers, and refusing to start when the
/// configuration cannot serve what the code asks for.
/// </summary>
public sealed class RegistrationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    /// <summary>One local provider, one hosted, and a role on each.</summary>
    private static IConfiguration TwoProviders() => Config(
        ("Agentry:Providers:local:Endpoint", "http://localhost:11434/v1"),
        ("Agentry:Providers:openai:Endpoint", "https://api.openai.com/v1"),
        ("Agentry:Providers:openai:ApiKeyVariable", "TEST_OPENAI_KEY"),
        ("Agentry:DefaultProvider", "local"),
        ("Agentry:Models:cheap", "qwen3:latest"),
        ("Agentry:Models:accurate:Provider", "openai"),
        ("Agentry:Models:accurate:Model", "gpt-5-mini"));

    /// <summary>Records every binding it was asked to build a client for.</summary>
    private sealed class Factory
    {
        public List<ModelBinding> Built { get; } = [];

        public IChatClient Create(ModelBinding binding, IServiceProvider sp)
        {
            Built.Add(binding);
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
    public void Two_roles_can_live_on_two_providers()
    {
        var services = new ServiceCollection();
        var factory = new Factory();

        services.AddAgentryModels(TwoProviders(), factory.Create);
        var router = services.BuildServiceProvider().GetRequiredService<IModelRouter>();

        router.For("cheap");
        router.For("accurate");

        var cheap = factory.Built.Single(b => b.Role == "cheap");
        var accurate = factory.Built.Single(b => b.Role == "accurate");

        Assert.Equal("http://localhost:11434/v1", cheap.Provider.Endpoint);
        Assert.Equal("https://api.openai.com/v1", accurate.Provider.Endpoint);
        Assert.Equal("gpt-5-mini", accurate.Model);
    }

    [Fact]
    public void A_bare_string_means_the_default_provider()
    {
        var services = new ServiceCollection();
        var factory = new Factory();

        services.AddAgentryModels(TwoProviders(), factory.Create);
        services.BuildServiceProvider().GetRequiredService<IModelRouter>().For("cheap");

        // The short form staying short is the point: most apps have one
        // provider, and making them write an object to say so is a tax on the
        // common case.
        Assert.Equal("local", factory.Built.Single().Provider.Name);
    }

    [Fact]
    public void One_provider_needs_no_DefaultProvider()
    {
        var services = new ServiceCollection();
        var factory = new Factory();

        services.AddAgentryModels(
            Config(("Agentry:Providers:local:Endpoint", "http://localhost:11434/v1"),
                   ("Agentry:Models:cheap", "qwen3:latest")),
            factory.Create);

        services.BuildServiceProvider().GetRequiredService<IModelRouter>().For("cheap");

        // Unambiguous, so do not make a single-provider app declare which one.
        Assert.Equal("local", factory.Built.Single().Provider.Name);
    }

    [Fact]
    public void Two_providers_and_no_default_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentryModels(
                Config(("Agentry:Providers:a:Endpoint", "http://a"),
                       ("Agentry:Providers:b:Endpoint", "http://b"),
                       ("Agentry:Models:cheap", "m")),
                new Factory().Create));

        Assert.Contains("names no provider", error.Message);
        Assert.Contains("DefaultProvider", error.Message);
    }

    [Fact]
    public void A_role_on_an_unknown_provider_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentryModels(
                Config(("Agentry:Providers:local:Endpoint", "http://localhost:11434/v1"),
                       ("Agentry:Models:accurate:Provider", "opeani"),
                       ("Agentry:Models:accurate:Model", "gpt-5-mini")),
                new Factory().Create));

        // A typo names what does exist, because otherwise it is indistinguishable
        // from a provider somebody forgot to add.
        Assert.Contains("opeani", error.Message);
        Assert.Contains("local", error.Message);
    }

    [Fact]
    public void A_provider_without_an_endpoint_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentryModels(
                Config(("Agentry:Providers:local:ApiKeyVariable", "KEY"),
                       ("Agentry:Models:cheap", "m")),
                new Factory().Create));

        // Otherwise it surfaces as a malformed-URI exception from inside an SDK.
        Assert.Contains("no Endpoint", error.Message);
    }

    [Fact]
    public void The_key_comes_from_the_environment_and_not_the_file()
    {
        Environment.SetEnvironmentVariable("TEST_OPENAI_KEY", "sk-from-the-environment");

        try
        {
            var services = new ServiceCollection();
            var factory = new Factory();

            services.AddAgentryModels(TwoProviders(), factory.Create);
            services.BuildServiceProvider().GetRequiredService<IModelRouter>().For("accurate");

            // The file names where the key lives; it never holds one.
            Assert.Equal("sk-from-the-environment", factory.Built.Single().Provider.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_OPENAI_KEY", null);
        }
    }

    [Fact]
    public void Clients_are_built_lazily_and_once()
    {
        var services = new ServiceCollection();
        var factory = new Factory();

        services.AddAgentryModels(TwoProviders(), factory.Create);
        var provider = services.BuildServiceProvider();

        // Registering ten models should not open ten connections.
        Assert.Empty(factory.Built);

        var router = provider.GetRequiredService<IModelRouter>();
        router.For("cheap");
        router.For("cheap");

        Assert.Single(factory.Built);
    }

    [Fact]
    public void An_unconfigured_role_names_the_ones_that_exist()
    {
        var services = new ServiceCollection();
        services.AddAgentryModels(TwoProviders(), new Factory().Create);

        var router = services.BuildServiceProvider().GetRequiredService<IModelRouter>();
        var error = Assert.Throws<ArgumentException>(() => router.For("acurate"));

        Assert.Contains("acurate", error.Message);
        Assert.Contains("accurate", error.Message);
    }

    [Fact]
    public void A_role_used_in_code_but_not_configured_fails_at_registration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentryModels(TwoProviders(), new Factory().Create)
                    .ValidateRoles(["accurate", "cheap", "enormous"]));

        // A startup failure, not a request that dies halfway through minutes
        // after deploy and reads as a missing service.
        Assert.Contains("enormous", error.Message);
    }

    [Fact]
    public void Everything_configured_validates_quietly()
    {
        var services = new ServiceCollection();

        services.AddAgentryModels(TwoProviders(), new Factory().Create)
                .ValidateRoles(["accurate", "cheap"]);
    }

    [Fact]
    public void A_configured_role_nothing_asks_for_does_not_stop_startup()
    {
        var services = new ServiceCollection();

        // Dead configuration is worth noticing and not worth refusing to start
        // over — a role can outlive the code that used it by one deploy.
        services.AddAgentryModels(TwoProviders(), new Factory().Create)
                .ValidateRoles(["cheap"]);
    }

    /// <summary>One provider declared, but nothing routed to it.</summary>
    private static IConfiguration UnusedProvider() => Config(
        ("Agentry:Providers:local:Endpoint", "http://localhost:11434/v1"),
        ("Agentry:Providers:openai:Endpoint", "https://api.openai.com/v1"),
        ("Agentry:Providers:openai:ApiKeyVariable", "TEST_OPENAI_KEY"),
        ("Agentry:DefaultProvider", "local"),
        ("Agentry:Models:cheap", "qwen3:latest"));

    [Fact]
    public void A_provider_nothing_routes_to_is_not_nagged_about()
    {
        var warnings = Warnings(() =>
            new ServiceCollection()
                .AddAgentryModels(UnusedProvider(), new Factory().Create)
                .ValidateRoles(["cheap"]));

        // Declaring a provider you are not routing to today is a legitimate
        // pattern — the samples list local and openai side by side precisely so
        // a role can be flipped with an environment variable. Warning that the
        // unused one has no key is noise about a call nothing will make, and it
        // reads as a problem at the top of every run.
        Assert.DoesNotContain("TEST_OPENAI_KEY", warnings);
    }

    [Fact]
    public void A_provider_a_role_does_use_is_still_nagged_about()
    {
        Environment.SetEnvironmentVariable("TEST_OPENAI_KEY", null);

        var warnings = Warnings(() =>
            new ServiceCollection()
                .AddAgentryModels(TwoProviders(), new Factory().Create)
                .ValidateRoles(["accurate", "cheap"]));

        // "accurate" binds to openai, so the missing key will fail the first
        // call that needs it. That is worth saying at startup.
        Assert.Contains("TEST_OPENAI_KEY", warnings);
    }

    [Fact]
    public void A_key_written_in_configuration_wins()
    {
        Environment.SetEnvironmentVariable("TEST_OPENAI_KEY", "from-the-environment");

        var configured = Config(
            ("Agentry:Providers:openai:Endpoint", "https://api.openai.com/v1"),
            ("Agentry:Providers:openai:ApiKeyVariable", "TEST_OPENAI_KEY"),
            ("Agentry:Providers:openai:ApiKey", "from-the-file"),
            ("Agentry:DefaultProvider", "openai"),
            ("Agentry:Models:accurate:Provider", "openai"),
            ("Agentry:Models:accurate:Model", "gpt-5-mini"));

        var agentry = new ServiceCollection().AddAgentryModels(configured, new Factory().Create);

        Assert.Equal("from-the-file", agentry.Providers["openai"].ApiKey);
        Assert.Equal("configuration", agentry.Providers["openai"].KeySource);
    }

    [Fact]
    public void A_key_absent_from_configuration_does_not_blank_the_environment()
    {
        Environment.SetEnvironmentVariable("TEST_OPENAI_KEY", "from-the-environment");

        // The whole safety property. If absent counted as empty, the committed
        // appsettings.json — which has no ApiKey in it and should not — would
        // override a real credential and turn every run into a 401 that reads
        // like a bad key rather than a missing one.
        var agentry = new ServiceCollection().AddAgentryModels(TwoProviders(), new Factory().Create);

        Assert.Equal("from-the-environment", agentry.Providers["openai"].ApiKey);
        Assert.Equal("TEST_OPENAI_KEY", agentry.Providers["openai"].KeySource);
    }

    private static string Warnings(Action act)
    {
        var original = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            act();
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
    }
}
