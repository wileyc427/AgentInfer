namespace AgentInfer;

/// <summary>
/// Generates a tool invoker for this type, with no agent attached.
/// </summary>
/// <remarks>
/// <para>
/// The two generated halves are not equally worth generating. An agent
/// implementation is a prompt constant, a constructor and one call site per
/// method — easy enough to write by hand. The tool half is a JSON Schema per
/// tool, a permission list, and a dispatch switch binding each argument from
/// its static type: derived data, which rots when maintained by hand.
/// </para>
/// <para>
/// This attribute takes the second without the first.
/// <c>[Agent(Tools = typeof(X))]</c> still generates both; this generates only
/// the invoker and its manifest, for a hand-written agent to use.
/// </para>
/// <para>
/// An attribute argument must be a compile-time constant, so a prompt composed
/// at run time, a role chosen from the input, or a retry that feeds a bind
/// failure back to the model cannot be declared. Where the generator fits, use
/// it; where it does not, an ordinary class reaches the same runtime.
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
