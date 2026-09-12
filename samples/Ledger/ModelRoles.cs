namespace Ledger;

/// <summary>
/// The model roles this app's agents ask for.
/// </summary>
/// <remarks>
/// <para>
/// Yours to define, not generated — and that is a constraint rather than an
/// omission. The generator learns a role <em>by reading the attribute</em>, so
/// a constant it emitted could not be used in the attribute that produced it.
/// The dependency only runs one way.
/// </para>
/// <para>
/// What is generated is <c>AgentInferRoles.All</c>: the set of roles actually
/// asked for, which is the other half. These constants make a rename a rename;
/// that array makes a missing registration a startup failure.
/// </para>
/// <para>
/// <c>const</c> rather than <c>static readonly</c>, because an attribute
/// argument must be a compile-time constant.
/// </para>
/// </remarks>
public static class ModelRoles
{
    /// <summary>Reasoning over figures. Worth a larger model.</summary>
    public const string SmallLlm = "smallllm";
}
