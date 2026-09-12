namespace AgentInfer;

/// <summary>How a generation method produces its result.</summary>
/// <remarks>
/// <para>
/// The absence of <see cref="StrategyAttribute"/> means <see cref="Predict"/>,
/// and that default is the most important decision in this library.
/// </para>
/// <para>
/// The framework this is modeled on defaults to executing model-written code,
/// which meant three methods in a project built against it were running
/// generated Python that nobody had reviewed — discovered by accident, after
/// its own documentation had claimed otherwise. Opting into code execution has
/// to be a line somebody typed, that a reviewer sees in a diff, and that
/// <c>grep</c> finds across a solution.
/// </para>
/// </remarks>
public enum Strategies
{
    /// <summary>
    /// One request, one response, bound to the return type. Nothing executes.
    /// </summary>
    Predict = 0,

    /// <summary>
    /// The model writes code that calls the agent's tools; a sandbox runs it.
    /// Requires a registered sandbox — a host with a CodeAct agent and no
    /// sandbox fails at startup rather than on the first call.
    /// </summary>
    /// <remarks>Not implemented in P1. Reserved so the value is stable.</remarks>
    CodeAct = 1,
}
