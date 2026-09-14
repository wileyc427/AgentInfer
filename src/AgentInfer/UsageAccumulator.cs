using Microsoft.Extensions.AI;

namespace AgentInfer;

/// <summary>
/// Collects what one generation method call spent, across every request it made.
/// </summary>
/// <remarks>
/// <para>
/// A generation method is not a request. A typed one adds the binding call, a
/// tool-using one adds a request per round, and a repaired one adds however
/// many the repair took. All of those belong to the same method call, and the
/// number worth having is their sum — so something has to survive from the
/// method's first request to its last, and <see cref="AgentRunner"/> has no
/// per-call object on the paths that use no tools.
/// </para>
/// <para>
/// Ambient for the same reason <see cref="AgentScope"/> is: the requests it
/// collects are sent by <c>FunctionInvokingChatClient</c>, which was handed a
/// client and knows nothing about the method that built it. The accumulator is
/// how the client hands a number back up to the runner without the runner
/// reaching down into a loop it does not own.
/// </para>
/// <para>
/// Nested rather than replaced, so an agent reached as another agent's tool
/// bills its tokens to its own operation instead of its caller's. The workflow
/// total is not lost by that: <see cref="AgentScope"/> rolls usage up through
/// its parents independently.
/// </para>
/// </remarks>
internal sealed class UsageAccumulator : IDisposable
{
    private static readonly AsyncLocal<UsageAccumulator?> Ambient = new();

    private readonly UsageAccumulator? _parent;

    // UsageDetails.Add mutates, and the tool loop can report from more than one
    // request in flight. A lock rather than Interlocked on each field because
    // Add is what keeps AdditionalCounts and the modality-specific counts
    // working without this type having to know they exist.
    private readonly object _gate = new();
    private readonly UsageDetails _usage = new();

    private int _reports;
    // An int rather than a bool so Dispose can claim it atomically, as in AgentScope.
    private int _closed;

    private UsageAccumulator(UsageAccumulator? parent) => _parent = parent;

    /// <summary>The accumulator for the method call on this execution context, if any.</summary>
    internal static UsageAccumulator? Current => Ambient.Value;

    /// <summary>Opens an accumulator for one generation method call.</summary>
    internal static UsageAccumulator Begin()
    {
        var accumulator = new UsageAccumulator(Ambient.Value);
        Ambient.Value = accumulator;
        return accumulator;
    }

    /// <summary>
    /// Whether any request reported usage at all.
    /// </summary>
    /// <remarks>
    /// Distinct from a zero count on purpose. Plenty of providers report
    /// nothing — a local Ollama streaming without <c>include_usage</c>, for one
    /// — and logging <c>0 in / 0 out</c> for those would read as a free call
    /// rather than an unmeasured one.
    /// </remarks>
    internal bool Reported => Volatile.Read(ref _reports) > 0;

    internal void Add(UsageDetails details)
    {
        lock (_gate)
        {
            _usage.Add(details);
            _reports++;
        }
    }

    /// <summary>What the method call spent, as of now.</summary>
    internal TokenCounts Snapshot()
    {
        lock (_gate)
        {
            var input = _usage.InputTokenCount ?? 0;
            var output = _usage.OutputTokenCount ?? 0;

            return new TokenCounts(
                input,
                output,
                // Providers that report the two halves and not the sum are
                // common enough that trusting the null would report a call with
                // thousands of tokens as costing zero.
                _usage.TotalTokenCount ?? input + output,
                _usage.ReasoningTokenCount ?? 0,
                _usage.CachedInputTokenCount ?? 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        Ambient.Value = _parent;
    }
}

/// <summary>What a call or a workflow spent, in tokens.</summary>
/// <param name="Input">Tokens in the prompt, summed across requests.</param>
/// <param name="Output">Tokens the model generated, summed across requests.</param>
/// <param name="Total">The provider's total where it gave one, else input plus output.</param>
/// <param name="Reasoning">
/// Output tokens spent thinking rather than answering, where the provider
/// distinguishes them. Usually the gap between a reply's length and its
/// latency.
/// </param>
/// <param name="CachedInput">
/// Input tokens served from a provider's prompt cache. Billed differently
/// everywhere that offers it, so a cost figure that ignores it is wrong in the
/// expensive direction.
/// </param>
public readonly record struct TokenCounts(
    long Input,
    long Output,
    long Total,
    long Reasoning,
    long CachedInput);
