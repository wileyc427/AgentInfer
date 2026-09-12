namespace AgentInfer;

/// <summary>
/// Resolves a model role to the runner that serves it.
/// </summary>
/// <remarks>
/// One method rather than a registry type, so the obvious implementation is a
/// dictionary and the DI-flavored one is a lambda over keyed services:
/// <code>
/// services.AddKeyedSingleton&lt;AgentRunner&gt;("accurate", …);
/// services.AddSingleton&lt;IModelRouter&gt;(sp =&gt;
///     new DelegateModelRouter(role =&gt; sp.GetRequiredKeyedService&lt;AgentRunner&gt;(role)));
/// </code>
/// </remarks>
public interface IModelRouter
{
    /// <summary>The runner for this role.</summary>
    /// <exception cref="ArgumentException">The role is not configured.</exception>
    public AgentRunner For(string role);
}

/// <summary>A router built from a dictionary of roles.</summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly Dictionary<string, AgentRunner> _runners;

    public ModelRouter(IEnumerable<KeyValuePair<string, AgentRunner>> runners) =>
        _runners = new Dictionary<string, AgentRunner>(
            runners ?? throw new ArgumentNullException(nameof(runners)),
            StringComparer.Ordinal);

    public AgentRunner For(string role) =>
        _runners.TryGetValue(role, out var runner)
            ? runner
            // Named rather than generic, and listing what exists: a typo in a
            // role is otherwise indistinguishable from a missing registration,
            // and both present as a null reference somewhere else.
            : throw new ArgumentException(
                $"No model is configured for the role '{role}'. Configured: {string.Join(", ", _runners.Keys)}.",
                nameof(role));
}

/// <summary>A router backed by a function, for DI.</summary>
public sealed class DelegateModelRouter(Func<string, AgentRunner> resolve) : IModelRouter
{
    private readonly Func<string, AgentRunner> _resolve =
        resolve ?? throw new ArgumentNullException(nameof(resolve));

    public AgentRunner For(string role) => _resolve(role);
}
