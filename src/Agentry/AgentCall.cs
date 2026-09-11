using System.Diagnostics.CodeAnalysis;

namespace Agentry;

/// <summary>
/// One call to a generation method, as the runtime sees it.
/// </summary>
/// <remarks>
/// Assembled entirely by generated code. It exists so the runtime has no reason
/// to reflect over anything: the prompts are constants the generator copied, the
/// arguments were rendered by generated code that knew their static types, and
/// the return type arrives as a type parameter rather than a <see cref="Type"/>
/// to look up.
/// <para>
/// A record so derived calls can be built with <c>with</c>. The two-phase JSON
/// path copies a call and changes two fields, and doing that by hand is how the
/// response schema got left off it the first time.
/// </para>
/// </remarks>
public sealed record AgentCall
{
    public required string SystemPrompt { get; init; }

    public required string TaskPrompt { get; init; }

    /// <summary>The method's arguments, already rendered, in declaration order.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Arguments { get; init; }

    /// <summary>
    /// Names the agent and method for logs and traces — <c>ILedgerAnalyst.SummariseAsync</c>.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// JSON Schema for what the model must produce, when the return type has a
    /// shape. Empty for a text return.
    /// </summary>
    /// <remarks>
    /// Generated at compile time from the return type and used twice: set as
    /// the provider's response format where that is supported, and included in
    /// the prompt where it is not. Both, because the two fail in different
    /// places and a model that ignores one often honours the other.
    /// </remarks>
    public string ResponseSchema { get; init; } = string.Empty;
}

/// <summary>Thrown when a generation method cannot produce a usable result.</summary>
public sealed class AgentException : Exception
{
    public AgentException(string operation, string message, Exception? inner = null)
        : base($"{operation}: {message}", inner) => Operation = operation;

    public string Operation { get; }
}

/// <summary>
/// Marker for the P1 JSON path, which still binds results reflectively.
/// </summary>
/// <remarks>
/// Flagged rather than quietly allowed. The library claims to work under AOT and
/// trimming, and this is the one place that claim does not hold yet — so it says
/// so in the type system and the warning travels to whoever calls it. The fix is
/// a generated <c>JsonSerializerContext</c>; until then the honest state is a
/// documented exception, not a silent one.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
[ExcludeFromCodeCoverage]
internal sealed class NotYetAotSafeAttribute : Attribute;
