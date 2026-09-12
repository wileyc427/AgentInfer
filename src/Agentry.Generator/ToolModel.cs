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

    /// <summary>
    /// The expression that turns this tool's result into text, already chosen.
    /// </summary>
    /// <remarks>
    /// <c>ToolResult.Render(result)</c> for a shape the compiler can pick an
    /// overload for, or a <c>JsonSerializer.Serialize</c> against a
    /// <c>JsonTypeInfo</c> from the assembly's context. Decided here, where the
    /// return type is a symbol, so Emit never has to ask a type anything.
    /// </remarks>
    string Renderer,

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
    string Reader,

    /// <summary>
    /// The C# source for this parameter's default, or <c>null</c> when it has
    /// none. Non-null means the argument is absent from the schema's
    /// <c>required</c> list, so a model may legitimately omit it.
    /// </summary>
    string? Default) : IEquatable<ToolParameterModel>;

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
