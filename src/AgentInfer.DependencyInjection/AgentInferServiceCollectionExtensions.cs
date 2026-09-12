using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentInfer;

/// <summary>
/// Registers model roles and the router that resolves them.
/// </summary>
/// <remarks>
/// <para>
/// The mapping from a role to a model is deployment configuration, so it lives
/// in <c>IConfiguration</c> and not in an attribute. What this adds is the loop
/// every consumer would otherwise write, and the checks that would otherwise be
/// skipped.
/// </para>
/// <para>
/// It does not build clients. Constructing an <see cref="IChatClient"/> is
/// provider-specific — SDK, credential type, options — and a library that
/// guessed would be wrong for everyone but its author. The caller supplies a
/// factory; this supplies everything the factory needs to decide.
/// </para>
/// <example>
/// <code>
/// {
///   "AgentInfer": {
///     "DefaultProvider": "local",
///     "Providers": {
///       "local":  { "Endpoint": "http://localhost:11434/v1" },
///       "openai": { "Endpoint": "https://api.openai.com/v1", "ApiKeyVariable": "OPENAI_API_KEY" }
///     },
///     "Models": {
///       "accurate": { "Provider": "openai", "Model": "gpt-5-mini" },
///       "cheap": "qwen3:latest"
///     }
///   }
/// }
/// </code>
/// A role may be a bare string, which means the default provider. The short
/// form staying short is the point: most apps have one provider, and making
/// them write an object to say so would be a tax on the common case.
/// </example>
/// </remarks>
public static class AgentInferServiceCollectionExtensions
{
    /// <summary>The configuration section read by default.</summary>
    public const string DefaultSection = "AgentInfer";

    public static AgentInferBuilder AddAgentInferModels(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<ModelBinding, IServiceProvider, IChatClient> clientFactory,
        string sectionName = DefaultSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clientFactory);

        var section = configuration.GetSection(sectionName);
        var providers = ReadProviders(section, sectionName);
        var bindings = ReadModels(section, providers, sectionName);

        foreach (var (role, binding) in bindings)
        {
            services.AddKeyedSingleton(role, (sp, _) =>
                new AgentRunner(clientFactory(binding, sp), sp.GetService<ILogger<AgentRunner>>()));
        }

        services.AddSingleton<IModelRouter>(sp => new DelegateModelRouter(
            role => sp.GetKeyedService<AgentRunner>(role)
                    ?? throw new ArgumentException(
                        $"No model is configured for the role '{role}'. "
                        + $"Add it under {sectionName}:Models. Configured: {Listed(bindings.Keys)}.",
                        nameof(role))));

        return new AgentInferBuilder(services, sectionName, bindings, providers);
    }

    private static Dictionary<string, ProviderOptions> ReadProviders(
        IConfigurationSection section,
        string sectionName)
    {
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.Ordinal);

        foreach (var child in section.GetSection("Providers").GetChildren())
        {
            var endpoint = child["Endpoint"];

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                // At registration, because a provider without an address cannot
                // serve anything and the failure is otherwise a malformed-URI
                // exception from inside an SDK.
                throw new InvalidOperationException(
                    $"The provider '{child.Key}' has no Endpoint under {sectionName}:Providers.");
            }

            // ApiKey is read as well as ApiKeyVariable, so a local run can put
            // one in appsettings.Development.json rather than an export. See
            // ProviderOptions.ApiKey for why a key that is absent never blanks
            // one that is present.
            providers[child.Key] = new ProviderOptions(
                child.Key, endpoint, child["ApiKeyVariable"], child["ApiKey"]);
        }

        return providers;
    }

    private static Dictionary<string, ModelBinding> ReadModels(
        IConfigurationSection section,
        Dictionary<string, ProviderOptions> providers,
        string sectionName)
    {
        var defaultProvider = section["DefaultProvider"];
        var bindings = new Dictionary<string, ModelBinding>(StringComparer.Ordinal);

        foreach (var child in section.GetSection("Models").GetChildren())
        {
            // A bare string is the short form: this model, on the default
            // provider. An object names its own.
            var model = child.Value ?? child["Model"];
            var providerName = child.Value is null ? child["Provider"] ?? defaultProvider : defaultProvider;

            if (string.IsNullOrWhiteSpace(model))
            {
                throw new InvalidOperationException(
                    $"The role '{child.Key}' has no Model under {sectionName}:Models.");
            }

            bindings[child.Key] = new ModelBinding(
                child.Key,
                model,
                Resolve(providerName, providers, child.Key, sectionName));
        }

        return bindings;
    }

    private static ProviderOptions Resolve(
        string? name,
        Dictionary<string, ProviderOptions> providers,
        string role,
        string sectionName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // One provider and no DefaultProvider is unambiguous, so do not
            // make a single-provider app declare which one it means.
            if (providers.Count == 1) return providers.Values.First();

            throw new InvalidOperationException(
                $"The role '{role}' names no provider and {sectionName}:DefaultProvider is not set. "
                + $"Providers: {Listed(providers.Keys)}.");
        }

        return providers.TryGetValue(name, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"The role '{role}' uses the provider '{name}', which is not configured under "
                + $"{sectionName}:Providers. Configured: {Listed(providers.Keys)}.");
    }

    private static string Listed(IEnumerable<string> names)
    {
        var listed = string.Join(", ", names);
        return listed.Length == 0 ? "(none)" : listed;
    }
}

/// <summary>What <c>AddAgentInferModels</c> returns, so registration can be checked.</summary>
public sealed class AgentInferBuilder(
    IServiceCollection services,
    string sectionName,
    IReadOnlyDictionary<string, ModelBinding> models,
    IReadOnlyDictionary<string, ProviderOptions> providers)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Every configured role, with its model and provider.</summary>
    public IReadOnlyDictionary<string, ModelBinding> Models { get; } = models;

    public IReadOnlyDictionary<string, ProviderOptions> Providers { get; } = providers;

    /// <summary>
    /// Fails now if any role the code asks for has no model configured.
    /// </summary>
    /// <param name="declared">
    /// <c>AgentInferRoles.All</c> — generated from the <c>[Model]</c> attributes in
    /// this assembly, so the check is against the roles actually used rather
    /// than a list somebody maintains.
    /// </param>
    /// <remarks>
    /// Thrown during registration rather than surfaced later, which makes it a
    /// startup failure without taking a dependency on hosting. A role resolved
    /// on first use fails halfway through somebody's request, minutes after
    /// deploy, and reads as a missing service rather than a missing line of
    /// config.
    /// </remarks>
    public AgentInferBuilder ValidateRoles(IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var wanted = declared.ToArray();
        var missing = wanted.Where(role => !Models.ContainsKey(role)).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"These model roles are used in code but not configured under {sectionName}:Models: "
                + $"{string.Join(", ", missing)}. Configured: "
                + $"{(Models.Count == 0 ? "(none)" : string.Join(", ", Models.Keys))}.");
        }

        // Dead configuration and a missing credential are both worth saying and
        // neither is worth refusing to start over: a role can outlive its code
        // by a deploy, and a key can arrive from somewhere this cannot see.
        var unused = Models.Keys.Where(role => !wanted.Contains(role, StringComparer.Ordinal)).ToArray();
        if (unused.Length > 0)
        {
            Console.Error.WriteLine(
                $"[AgentInfer] Configured model roles nothing asks for: {string.Join(", ", unused)}.");
        }

        // Only providers a configured role actually binds to. Declaring one you
        // are not routing to today is a legitimate pattern — the samples list
        // `local` and `openai` side by side precisely so a role can be flipped
        // with an environment variable — and warning that the unused one has no
        // key makes that pattern noisy about a call nothing is going to make.
        //
        // A dead *role* is different, which is why the warning above is not
        // scoped the same way: a role is named in code, so a configured one
        // nothing asks for really is stale.
        var routed = Models.Values
            .Select(binding => binding.Provider.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var provider in Providers.Values)
        {
            if (!routed.Contains(provider.Name)) continue;

            if (provider.ApiKeyVariable is { Length: > 0 } variable && provider.ApiKey is null)
            {
                Console.Error.WriteLine(
                    $"[AgentInfer] Provider '{provider.Name}' expects a key in {variable}, which is not set. "
                    + "Set it, or put ApiKey in appsettings.Development.json.");
            }
        }

        return this;
    }
}
