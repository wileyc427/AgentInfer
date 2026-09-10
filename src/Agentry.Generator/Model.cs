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
internal sealed record AgentModel(
    string Namespace,
    string InterfaceName,
    string ImplementationName,
    string Accessibility,
    string SystemPrompt,
    EquatableArray<MethodModel> Methods,
    EquatableArray<ToolModel> Tools,
    string ToolsType);

internal sealed record MethodModel(
    string Name,
    string TaskPrompt,
    ReturnShape Shape,
    string ReturnType,
    EquatableArray<ParameterModel> Parameters,
    string? CancellationTokenParameter) : IEquatable<MethodModel>;

internal sealed record ParameterModel(string Name, string Type, bool IsString) : IEquatable<ParameterModel>;

/// <summary>What the generated method has to do with the model's reply.</summary>
internal enum ReturnShape
{
    /// <summary><c>Task&lt;string&gt;</c> — hand the text back untouched.</summary>
    Text,

    /// <summary><c>Task&lt;T&gt;</c> — bind the reply as JSON.</summary>
    Json,
}
