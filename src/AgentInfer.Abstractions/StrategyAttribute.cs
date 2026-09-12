namespace AgentInfer;

/// <summary>
/// Chooses how a method produces its result. Absent means
/// <see cref="Strategies.Predict"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class StrategyAttribute : Attribute
{
    public StrategyAttribute(Strategies strategy) => Strategy = strategy;

    public Strategies Strategy { get; }

    /// <summary>
    /// The most cells a <see cref="Strategies.CodeAct"/> method may run before
    /// it gives up. Ignored by <see cref="Strategies.Predict"/>.
    /// </summary>
    /// <remarks>
    /// A bound rather than a suggestion. A writer and a critic left alone will
    /// trade opinions until something else stops them, and "something else" is
    /// usually a bill.
    /// </remarks>
    public int MaxIterations { get; set; } = 6;
}
