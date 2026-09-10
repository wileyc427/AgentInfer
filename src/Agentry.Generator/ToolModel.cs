namespace Agentry.Generator;

/// <summary>One <c>[AgentTool]</c> method, in cacheable form.</summary>
/// <remarks>
/// Same rule as <see cref="AgentModel"/>: strings and enums only. The JSON
/// schema is built during Transform, while symbols are still in hand, and
/// travels onward as text — so Emit never needs a type to ask questions of.
/// </remarks>
internal sealed record ToolModel(
    string Name,
    string Description,
    string ParametersSchema,
    EquatableArray<string> Permissions,
    EquatableArray<ToolParameterModel> Parameters,
    ToolReturn Return,
    bool TakesCancellationToken) : IEquatable<ToolModel>;

/// <summary>A tool parameter, with the reader that binds it.</summary>
/// <remarks>
/// <see cref="Reader"/> is the <c>JsonElement</c> accessor for this type —
/// <c>GetString()</c>, <c>GetInt32()</c> and so on — chosen at compile time
/// while the symbol was in hand. That is what keeps dispatch free of
/// reflection: the emitted switch reads each argument with an accessor that
/// already knows the static type.
/// </remarks>
internal sealed record ToolParameterModel(
    string Name,
    string Type,
    string Reader) : IEquatable<ToolParameterModel>;

/// <summary>What a tool hands back, and how the invoker renders it.</summary>
internal enum ToolReturn
{
    /// <summary><c>void</c> or <c>Task</c>: nothing to render.</summary>
    None,

    /// <summary>Synchronous value.</summary>
    Value,

    /// <summary><c>Task&lt;T&gt;</c>: await, then render.</summary>
    AwaitedValue,

    /// <summary><c>Task</c>: await, render nothing.</summary>
    AwaitedNone,
}
