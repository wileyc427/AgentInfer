using Microsoft.CodeAnalysis;

namespace Agentry.Generator;

/// <summary>
/// The build errors that replace the other framework's runtime surprises.
/// </summary>
/// <remarks>
/// This is the argument for the whole approach, so it is worth keeping the
/// provenance next to each rule. Every descriptor below is a failure that
/// actually happened while building a Python project against NOOA, moved from
/// production to the build.
/// <para>
/// The corollary is a design rule: <b>a feature that cannot be diagnosed at
/// compile time should be questioned before it is added.</b>
/// </para>
/// </remarks>
internal static class Diagnostics
{
    private const string Category = "Agentry";

    /// <summary>
    /// NOOA equivalent: an f-string is not a docstring, so the class silently
    /// inherits the framework's own internal prompt — about 1.5KB of CodeAct
    /// boilerplate — with no error anywhere.
    /// </summary>
    public static readonly DiagnosticDescriptor MissingAgentPrompt = new(
        id: "AGT001",
        title: "Agent requires a system prompt",
        messageFormat: "'{0}' has [Agent] with an empty prompt. The prompt is the only thing the model is told about who it is.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// NOOA equivalent: a method with no docstring gets an empty task prompt and
    /// behaves almost right, which is worse than failing.
    /// </summary>
    public static readonly DiagnosticDescriptor MissingMethodPrompt = new(
        id: "AGT002",
        title: "Generation method requires [Prompt]",
        messageFormat: "'{0}' is on an [Agent] interface but has no [Prompt]. Add one, or move the method off this interface.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// NOOA equivalent: a return annotation the strategy cannot satisfy fails on
    /// the first call, after the model has been paid for.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        id: "AGT003",
        title: "Unsupported return type",
        messageFormat: "'{0}' returns '{1}'. A generation method must return Task<T>; T is the contract the reply is bound to.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// The one that cost a week: NOOA's default strategy executes model-written
    /// code, so an undecorated method runs a REPL nobody asked for. Here CodeAct
    /// is opt-in and, until P3, saying so is an error rather than a surprise.
    /// </summary>
    public static readonly DiagnosticDescriptor CodeActNotAvailable = new(
        id: "AGT004",
        title: "CodeAct is not implemented",
        messageFormat: "'{0}' asks for Strategies.CodeAct, which needs a sandbox and a broker (P3). Use Predict, which is the default.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A parameter the schema builder cannot describe. Better here than as a
    /// model sending a shape the parameter cannot take, learned from a trace.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedToolParameter = new(
        id: "AGT005",
        title: "Unsupported tool parameter",
        messageFormat: "'{0}' takes '{1} {2}', which has no JSON schema. Use a scalar, an enum, or an array of those.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A prompt file the compiler cannot see. The file is sitting in the
    /// project and looks present, which makes this the one failure here that
    /// needs the fix in the message rather than a pointer to the docs.
    /// </summary>
    public static readonly DiagnosticDescriptor PromptFileNotFound = new(
        id: "AGT008",
        title: "Prompt file is not visible to the compiler",
        messageFormat: "'{0}' names prompt file '{1}'. Add <AdditionalFiles Include=\"{1}\" /> to the project; the compiler cannot see a file that is not listed there.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Two sources for one string. One of them is stale and nothing can say
    /// which, so picking a winner would be exactly the silent almost-right
    /// behaviour the rest of this file exists to prevent.
    /// </summary>
    public static readonly DiagnosticDescriptor ConflictingPromptSources = new(
        id: "AGT009",
        title: "Agent has both a prompt and a PromptFile",
        messageFormat: "'{0}' sets both a prompt and PromptFile '{1}'. Keep one; the other is already out of date.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Only reachable when the project directory is unknown and the path has to
    /// be matched by suffix. Named rather than folded into AGT008 because
    /// "found too many" and "found none" have different fixes.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousPromptFile = new(
        id: "AGT010",
        title: "Prompt file matches more than one AdditionalFiles entry",
        messageFormat: "'{0}' names prompt file '{1}', which matches {2} files. Make the path project-relative so it names one.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// NOOA equivalent: <c>@hidden</c> keeps a method out of the generated docs
    /// and leaves it perfectly callable, because an in-process object cannot
    /// make its own methods unreachable. Here every tool states its permission
    /// and the generator narrows what is bound — so the answer to "may this
    /// caller reach it" is decided before anything runs.
    /// </summary>
    public static readonly DiagnosticDescriptor MissingToolPermission = new(
        id: "AGT006",
        title: "Tool requires [RequiresPermission]",
        messageFormat: "'{0}' is an [AgentTool] with no [RequiresPermission]. Say what a caller must hold, even if it is a permission everyone has.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A role has to be something a router can be configured for. An empty one
    /// resolves to nothing and presents as a missing registration.
    /// </summary>
    public static readonly DiagnosticDescriptor EmptyModelRole = new(
        id: "AGT007",
        title: "Model requires a role",
        messageFormat: "'{0}' has [Model] with an empty role. Name what the method needs — \"accurate\", \"cheap\" — not a model id.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A <c>[Flags]</c> enum in a schema position. The member list describes a
    /// choice of one, so a set-valued type offered that way invites the reply
    /// <c>"Read, Write"</c> — which reads correctly and binds to nothing.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedFlagsEnum = new(
        id: "AGT008",
        title: "Flags enum has no JSON schema",
        messageFormat: "'{0}' uses '{1}', which is a [Flags] enum. JSON Schema describes a choice of one value, not a set — return an array of a plain enum instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
