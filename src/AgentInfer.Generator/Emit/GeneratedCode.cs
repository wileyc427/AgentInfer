using System.Reflection;

namespace AgentInfer.Generator.Emit;

/// <summary>
/// The <c>[GeneratedCode]</c> line that sits above every emitted type.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the <c>&lt;auto-generated/&gt;</c> header, which is a compiler
/// convention that silences analyzers and code style within the file. This is
/// the attribute other tooling reads: a coverage run can be pointed at it
/// (<c>--exclude-by-attribute</c>) so a consumer's numbers stop counting a
/// dispatch switch nobody wrote and nobody can meaningfully test.
/// </para>
/// <para>
/// It also records which generator wrote the file, which is the first question
/// asked of any <c>.g.cs</c> pasted into an issue — and the one the file itself
/// could not answer before.
/// </para>
/// </remarks>
internal static class GeneratedCode
{
    /// <summary>The generator's own assembly version, read once.</summary>
    /// <remarks>
    /// Read rather than written as a constant. A constant would have to be kept
    /// in step with <c>&lt;Version&gt;</c> by hand, and a hand-maintained copy
    /// of a value that already exists is the drift this library is built to
    /// remove.
    /// </remarks>
    private static readonly string Version =
        typeof(GeneratedCode).GetTypeInfo().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>The attribute, fully qualified, with no trailing newline.</summary>
    public static string Attribute { get; } =
        "[global::System.CodeDom.Compiler.GeneratedCodeAttribute("
        + "\"AgentInfer.Generator\", \"" + Version + "\")]";
}
