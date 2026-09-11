namespace Agentry.Generator;

/// <summary>
/// What the generator knows about one agent, in a form Roslyn can cache.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a string, an enum or an <see cref="EquatableArray{T}"/>.
/// That is the rule that makes an incremental generator incremental, and it is
/// worth stating as a rule because breaking it is easy and invisible:
/// </para>
/// <para>
/// <b>No <c>ISymbol</c>, no <c>SyntaxNode</c>, no <c>Compilation</c> may ever be
/// stored in a pipeline model.</b> Each of those roots the entire compilation
/// it came from, so caching one pins the previous compilation in memory — and
/// they do not compare by value, so the cache never hits anyway. You get a
/// generator that both leaks and re-runs constantly.
/// </para>
/// <para>
/// Symbols are used during <c>Transform</c> and thrown away. What survives is
/// this record.
/// </para>
/// </remarks>
/// <param name="SystemPrompt">
/// The prompt itself, whether it was written in the attribute or read from
/// <paramref name="PromptFile"/>. By the time this reaches the emitter the two
/// routes are indistinguishable, which is the point.
/// </param>
/// <param name="PromptFile">
/// The file the prompt was read from, for the header comment in the generated
/// source. Empty when the prompt was written inline.
/// </param>
internal sealed record AgentModel(
    string Namespace,
    string InterfaceName,
    string ImplementationName,
    string Accessibility,
    string SystemPrompt,
    string PromptFile,
    EquatableArray<MethodModel> Methods,
    EquatableArray<ToolModel> Tools,
    string ToolsType);

internal sealed record MethodModel(
    string Name,

    /// <summary>
    /// <c>ILedgerAnalyst.SummariseAsync</c> — what logs, spans and metrics are
    /// tagged with.
    /// </summary>
    /// <remarks>
    /// Qualified by the interface, and the bare method name was a real defect
    /// rather than a cosmetic one. <c>operation</c> is the tag on
    /// <c>calls_per_turn</c>, <c>tools.offered</c> and the workflow histograms,
    /// so two agents that both have a <c>SummariseAsync</c> — which is most of
    /// them — collapsed into one series. The instrumentation this repo calls
    /// the product was measuring the wrong thing.
    /// </remarks>
    string Operation,

    string TaskPrompt,
    ReturnShape Shape,
    string ReturnType,
    EquatableArray<ParameterModel> Parameters,
    string? CancellationTokenParameter,
    int MaxIterations,
    string ReturnSchema,
    string? ModelRole) : IEquatable<MethodModel>;

internal sealed record ParameterModel(string Name, string Type, bool IsString) : IEquatable<ParameterModel>;

/// <summary>What the generated method has to do with the model's reply.</summary>
internal enum ReturnShape
{
    /// <summary><c>Task&lt;string&gt;</c> — hand the text back untouched.</summary>
    Text,

    /// <summary><c>Task&lt;T&gt;</c> — bind the reply as JSON.</summary>
    Json,
}

/// <summary>
/// A type carrying <c>[AgentTools]</c>: tools with no agent attached.
/// </summary>
/// <remarks>
/// Same rule as <see cref="AgentModel"/> — strings and
/// <see cref="EquatableArray{T}"/> only, so the pipeline can cache it by value.
/// </remarks>
internal sealed record ToolsModel(
    string Namespace,
    string ToolsType,
    string InvokerName,
    string Accessibility,
    EquatableArray<ToolModel> Tools);
