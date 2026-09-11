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
public sealed record ProviderOptions(
    string Name,
    string Endpoint,
    string? ApiKeyVariable = null,
    string? ConfiguredKey = null)
{
    /// <summary>
    /// The credential for this provider, or null when it needs none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key written in configuration wins, and a configuration source that
    /// does not mention one falls through to the environment variable
    /// <see cref="ApiKeyVariable"/> names. Present beats absent; absent never
    /// blanks anything.
    /// </para>
    /// <para>
    /// That asymmetry is the whole safety property. If an absent key counted as
    /// an empty one, the committed <c>appsettings.json</c> — which has no
    /// <c>ApiKey</c> in it and should not — would override a real credential
    /// from the environment and turn every run into a 401 that reads like a
    /// bad key rather than a missing one.
    /// </para>
    /// <para>
    /// <b>A key in configuration belongs in a file that is not committed.</b>
    /// The samples read <c>appsettings.Development.json</c>, which is
    /// gitignored for exactly this. A plausible-looking value in a committed
    /// file is a value somebody eventually pastes a real one over.
    /// </para>
    /// </remarks>
    public string? ApiKey =>
        ConfiguredKey is { Length: > 0 } written
            ? written
            : ApiKeyVariable is { Length: > 0 } variable
                ? Environment.GetEnvironmentVariable(variable)
                : null;

    /// <summary>Where the credential came from, for a startup line to report.</summary>
    public string KeySource =>
        ConfiguredKey is { Length: > 0 } ? "configuration"
        : ApiKeyVariable is { Length: > 0 } variable
            ? (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } ? variable : $"{variable} (unset)")
            : "none needed";
}

/// <summary>A role, the model that serves it, and where that model lives.</summary>
/// <remarks>
/// What the client factory is handed. It carries the provider rather than just
/// the model name because a model name means nothing without an endpoint —
/// <c>gpt-5-mini</c> against a local Ollama is a 404, and the failure reads as a
/// missing model rather than a misrouted request.
/// </remarks>
public sealed record ModelBinding(string Role, string Model, ProviderOptions Provider);
