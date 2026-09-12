namespace AgentInfer;

/// <summary>
/// One tool, as the model is told about it. Generated; never written by hand.
/// </summary>
/// <remarks>
/// Lives in the runtime rather than in Abstractions on purpose. A domain
/// assembly needs the attributes and nothing else — the manifest is consumed by
/// generated code and the runtime, both of which already reference this
/// package. Keeping it out of Abstractions is what lets that assembly stay at
/// netstandard2.0 with no dependencies and no polyfills.
/// </remarks>
/// <remarks>
/// <para>
/// <see cref="ParametersSchema"/> is a JSON Schema string the generator built at
/// <b>compile time</b> from the method's parameters. That is the differentiating
/// claim of this library and the reason it can be trimmed and AOT-compiled: the
/// usual way to get here is reflecting over the method at startup, which works
/// until somebody publishes trimmed and the parameters vanish.
/// </para>
/// <para>
/// A record so a manifest can be compared in a test. The schema being a string
/// rather than a parsed object is deliberate — it is emitted once and handed
/// on, and nothing in this library ever needs to look inside it.
/// </para>
/// </remarks>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    string ParametersSchema,
    IReadOnlyList<string> Permissions);

/// <summary>Every tool one agent may reach.</summary>
public sealed record ToolManifest(IReadOnlyList<ToolDescriptor> Tools)
{
    public static ToolManifest Empty { get; } = new([]);
}
