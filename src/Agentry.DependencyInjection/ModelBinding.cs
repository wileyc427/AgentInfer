namespace Agentry;

/// <summary>
/// One place models are served from: an address, and where its key lives.
/// </summary>
/// <remarks>
/// <para>
/// Named once and shared, because several roles usually sit on one provider and
/// duplicating an endpoint per role is how two of them end up disagreeing.
/// </para>
/// <para>
/// <see cref="ApiKeyVariable"/> names the environment variable holding the
/// credential — it is <b>not</b> the credential. Configuration files get
/// committed, and a field shaped like a key is a field somebody eventually puts
/// a real one in. Naming the location keeps the secret out of the file while
/// still making it configurable per provider, which matters as soon as there is
/// more than one.
/// </para>
/// </remarks>
public sealed record ProviderOptions(string Name, string Endpoint, string? ApiKeyVariable = null)
{
    /// <summary>The credential for this provider, or null when it needs none.</summary>
    public string? ApiKey =>
        ApiKeyVariable is { Length: > 0 } variable
            ? Environment.GetEnvironmentVariable(variable)
            : null;
}

/// <summary>A role, the model that serves it, and where that model lives.</summary>
/// <remarks>
/// What the client factory is handed. It carries the provider rather than just
/// the model name because a model name means nothing without an endpoint —
/// <c>gpt-5-mini</c> against a local Ollama is a 404, and the failure reads as a
/// missing model rather than a misrouted request.
/// </remarks>
public sealed record ModelBinding(string Role, string Model, ProviderOptions Provider);
