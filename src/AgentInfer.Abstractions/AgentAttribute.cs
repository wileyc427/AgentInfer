namespace AgentInfer;

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
/// The cost, stated plainly: changing a prompt is a recompile, so prompts
/// cannot be swapped at run time. If that is ever wanted it arrives as an
/// explicit, opt-in provider — and a prompt loaded from outside the assembly is
/// untrusted input.
/// </para>
/// <para>
/// <see cref="PromptFile"/> moves the text out of the attribute without giving
/// any of that up: the file is read by the generator, at compile time, and
/// emitted as the same constant. It buys authoring ergonomics, not deployment
/// flexibility.
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

    /// <summary>
    /// Declares an agent whose prompt lives in a file. Set <see cref="PromptFile"/>.
    /// </summary>
    /// <remarks>
    /// A second constructor rather than a nullable parameter with a default,
    /// because <c>[Agent]</c> with no argument at all should be the shape that
    /// reads as "the prompt is elsewhere". Setting neither is AIN001.
    /// </remarks>
    public AgentAttribute()
    {
    }

    /// <summary>The system prompt, when it is written inline.</summary>
    public string? Prompt { get; }

    /// <summary>
    /// A file to read the system prompt from, relative to the project directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the generator during compilation and emitted as the same
    /// constant an inline prompt produces. Nothing is opened at run time, so
    /// trimming, AOT and the "the prompt in the binary is the prompt that ran"
    /// property all hold exactly as before. What it buys is markdown, no
    /// escaping, and prompt diffs that do not touch a <c>.cs</c> file.
    /// </para>
    /// <para>
    /// The compiler can only see files listed in <c>AdditionalFiles</c>. The
    /// package adds <c>Prompts/**/*.md</c> for you; anything else needs a line
    /// in the project file, and AIN008 says so with the line to paste.
    /// </para>
    /// <para>
    /// Setting this and a prompt together is AIN009. Two sources for one string
    /// means one of them is stale, and guessing which would be the kind of
    /// silent almost-right behavior this library exists to remove.
    /// </para>
    /// </remarks>
    public string? PromptFile { get; set; }

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
