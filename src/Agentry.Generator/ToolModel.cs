namespace Agentry.Generator;

/// <summary>One <c>[AgentTool]</c> method, in cacheable form.</summary>
/// <remarks>
/// Same rule as <see cref="AgentModel"/>: strings and enums only. The JSON
/// schema is built during Transform, while symbols are still in hand, and
/// travels as text — so Emit never needs a type to ask questions of.
/// </remarks>
internal sealed record ToolModel(
    string Name,
    string Description,
    string ParametersSchema,
    EquatableArray<string> Permissions) : IEquatable<ToolModel>;
