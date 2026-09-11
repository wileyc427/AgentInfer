namespace Incident;

/// <summary>The model roles this app's agents ask for.</summary>
/// <remarks>
/// Yours to define, not generated. The generator learns a role by reading the
/// attribute, so a constant it emitted could not be used in the attribute that
/// produced it — the dependency only runs one way. What <em>is</em> generated
/// is <c>AgentryRoles.All</c>, which makes a missing registration a startup
/// failure rather than a request that dies halfway through.
/// </remarks>
public static class ModelRoles
{
    /// <summary>Reasoning over findings. Worth a larger model.</summary>
    public const string Accurate = "accurate";
}
