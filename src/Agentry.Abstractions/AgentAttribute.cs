namespace Agentry;

/// <summary>
/// Marks an interface as an agent, and carries its system prompt.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a base class, for three reasons that all turned out
/// to matter in practice:
/// </para>
/// <list type="bullet">
///   <item>Consumers inject the interface, so mocking an agent in a test needs
///   no framework support and no model.</item>
///   <item>The prompt is a string constant. It survives compilation and
///   trimming, unlike a doc comment, which is stripped into a separate XML file
///   and unreachable at run time.</item>
///   <item>The implementation is generated to disk and can simply be read.</item>
/// </list>
/// <para>
/// The cost, stated plainly: changing a prompt is a recompile. That is close to
/// a wash against a docstring-based design, which needs a process restart
/// anyway, but it does mean prompts cannot be swapped at run time. If that is
/// ever wanted it arrives as an explicit, opt-in provider — and prompts loaded
/// from outside the assembly are untrusted input.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class AgentAttribute : Attribute
{
    /// <param name="prompt">
    /// The system prompt. Written for a model to act on, not for a developer to
    /// skim — this is the only thing the model is told about who it is.
    /// </param>
    public AgentAttribute(string prompt) => Prompt = prompt;

    /// <summary>The system prompt.</summary>
    public string Prompt { get; }

    /// <summary>
    /// Overrides the generated implementation's name. Defaults to the interface
    /// name without its leading <c>I</c>, suffixed <c>Agent</c>.
    /// </summary>
    public string? ImplementationName { get; set; }

    /// <summary>
    /// The type whose <see cref="AgentToolAttribute"/> methods this agent may call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named on the agent rather than discovered globally, because "what may
    /// this agent reach" is a property of the agent and should be readable
    /// where the agent is declared. A registry that collected every tool in the
    /// assembly would make the answer a grep instead of a line.
    /// </para>
    /// <para>
    /// This is also where the authorization story attaches: the generator emits
    /// one narrowed facade per permission set found on this type, and only the
    /// facade matching the principal is bound. A method the caller may not use
    /// is absent rather than refused.
    /// </para>
    /// </remarks>
    public Type? Tools { get; set; }
}
