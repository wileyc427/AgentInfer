using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentry;

/// <summary>
/// Registers model roles and the router that resolves them.
/// </summary>
/// <remarks>
/// <para>
/// The mapping from a role to a model name is deployment configuration, so it
/// lives in <c>IConfiguration</c> and not in an attribute. What this adds is
/// the loop every consumer would otherwise write, and the check that would
/// otherwise be skipped.
/// </para>
/// <para>
/// It does not build clients. Constructing an <see cref="IChatClient"/> is
/// provider-specific — endpoint, credential, SDK — and a library that guessed
/// would be wrong for everyone but its author. The caller supplies a factory
/// from a model name to a client, which is the one thing only they can know.
/// </para>
/// </remarks>
public static class AgentryServiceCollectionExtensions
{
    /// <summary>The configuration section read by default.</summary>
    public const string DefaultSection = "Agentry:Models";

    /// <summary>
    /// Registers one <see cref="AgentRunner"/> per configured role, keyed by
    /// role, plus an <see cref="IModelRouter"/> over them.
    /// </summary>
    /// <param name="clientFactory">
    /// Builds a client for one model name. Called once per role, lazily.
    /// </param>
    /// <example>
    /// <code>
    /// // appsettings.json
    /// // { "Agentry": { "Models": { "accurate": "claude-sonnet-5", "cheap": "qwen3:8b" } } }
    ///
    /// services.AddAgentryModels(config, (model, sp) => ClientFor(model))
    ///         .ValidateRoles(AgentryRoles.All);
    /// </code>
    /// </example>
    public static AgentryBuilder AddAgentryModels(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<string, IServiceProvider, IChatClient> clientFactory,
        string sectionName = DefaultSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clientFactory);

        var section = configuration.GetSection(sectionName);
        var models = section.GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => child.Value!, StringComparer.Ordinal);

        foreach (var (role, model) in models)
        {
            services.AddKeyedSingleton(role, (sp, _) =>
                new AgentRunner(clientFactory(model, sp), sp.GetService<ILogger<AgentRunner>>()));
        }

        services.AddSingleton<IModelRouter>(sp => new DelegateModelRouter(
            role => sp.GetKeyedService<AgentRunner>(role)
                    ?? throw new ArgumentException(
                        $"No model is configured for the role '{role}'. "
                        + $"Add it under {sectionName}. Configured: {Listed(models.Keys)}.",
                        nameof(role))));

        return new AgentryBuilder(services, sectionName, models);
    }

    private static string Listed(IEnumerable<string> roles)
    {
        var listed = string.Join(", ", roles);
        return listed.Length == 0 ? "(none)" : listed;
    }
}

/// <summary>What <c>AddAgentryModels</c> returns, so registration can be checked.</summary>
public sealed class AgentryBuilder(
    IServiceCollection services,
    string sectionName,
    IReadOnlyDictionary<string, string> models)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>The roles configured, in the order the section listed them.</summary>
    public IReadOnlyDictionary<string, string> Models { get; } = models;

    /// <summary>
    /// Fails now if any role the code asks for has no model configured.
    /// </summary>
    /// <param name="declared">
    /// <c>AgentryRoles.All</c> — generated from the <c>[Model]</c> attributes in
    /// this assembly, so the check is against the roles actually used rather
    /// than a list somebody maintains.
    /// </param>
    /// <remarks>
    /// <para>
    /// Thrown during registration rather than surfaced later, which makes it a
    /// startup failure without taking a dependency on hosting. A role resolved
    /// on first use fails halfway through somebody's request, minutes after
    /// deploy, and reads as a missing service rather than a missing line of
    /// config.
    /// </para>
    /// <para>
    /// The reverse — a configured role nothing asks for — is a warning on the
    /// console rather than a throw. It is dead configuration, which is worth
    /// noticing and is not worth refusing to start over.
    /// </para>
    /// </remarks>
    public AgentryBuilder ValidateRoles(IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var wanted = declared.ToArray();
        var missing = wanted.Where(role => !Models.ContainsKey(role)).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"These model roles are used in code but not configured under {sectionName}: "
                + $"{string.Join(", ", missing)}. Configured: "
                + $"{(Models.Count == 0 ? "(none)" : string.Join(", ", Models.Keys))}.");
        }

        var unused = Models.Keys.Where(role => !wanted.Contains(role, StringComparer.Ordinal)).ToArray();
        if (unused.Length > 0)
        {
            Console.Error.WriteLine(
                $"[Agentry] Configured model roles nothing asks for: {string.Join(", ", unused)}.");
        }

        return this;
    }
}
