namespace Agentry;

/// <summary>
/// Generates a tool invoker for this type, with no agent attached.
/// </summary>
/// <remarks>
/// <para>
/// The generated half of an agent splits unevenly. The implementation class is
/// a prompt constant, a constructor and one call site per method — mechanical,
/// low-risk, and pleasant enough to write by hand. The tool half is a JSON
/// Schema per tool, a permission list, and a dispatch switch that binds each
/// argument with an accessor chosen from its static type. That is derived data,
/// and derived data written by hand is the thing that rots.
/// </para>
/// <para>
/// So this attribute exists to let you take the second without the first.
/// <c>[Agent(Tools = typeof(X))]</c> still works and still generates both; this
/// generates only the invoker and its manifest, for a hand-written agent to
/// use.
/// </para>
/// <para>
/// <b>Why that is worth supporting rather than discouraging.</b> An attribute
/// argument must be a compile-time constant, which means a prompt composed at
/// run time, a model role chosen from the input, or a retry that feeds a bind
/// failure back to the model cannot be expressed declaratively — and should not
/// be bent into an attribute to avoid writing a class. Where the generator
/// fits, use it. Where it does not, the escape hatch is an ordinary class, and
/// nothing about the runtime requires generated code to reach it.
/// </para>
/// <para>
/// It is also the honest test of the library's own claim. If the runtime is the
/// product and the generator is a convenience, then a hand-written agent has to
/// be a first-class thing rather than a thing that technically compiles.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [AgentTools]
/// public sealed class IntakeTools
/// {
///     [AgentTool("Every ticket waiting in the queue.")]
///     [RequiresPermission("intake.read")]
///     public string[] Waiting() => ...;
/// }
///
/// // Generated: IntakeToolsInvoker, with IntakeToolsInvoker.Tools as the manifest.
/// var invoker = new IntakeToolsInvoker(new IntakeTools());
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AgentToolsAttribute : Attribute
{
    /// <summary>
    /// Overrides the generated invoker's name. Defaults to the type name
    /// suffixed <c>Invoker</c>.
    /// </summary>
    public string? InvokerName { get; set; }
}
