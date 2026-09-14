using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;

namespace AgentInfer;

/// <summary>
/// Counts every request against the enclosing <see cref="AgentScope"/>, refuses
/// the one that would exceed its bound, and collects what each one spent.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DelegatingChatClient"/> rather than a check inside
/// <see cref="AgentRunner"/>, because this is the only place every request is
/// visible. A tool-using method sends one request per round of tool calls, and
/// those rounds belong to <c>FunctionInvokingChatClient</c>, which does not
/// report them.
/// </para>
/// <para>
/// Applied by the runner to its own client rather than registered by a host: an
/// <c>AddAgentInferBudget()</c> a consumer wires up is more idiomatic and has
/// one fatal property — forget it and
/// <c>AgentScope.Begin("x", maxRequests: 20)</c> compiles, reads correctly, and
/// does nothing.
/// </para>
/// <para>
/// Stateless, since the counts live on the ambient scope, so one instance per
/// runner costs an allocation at construction rather than per call.
/// </para>
/// <para>
/// It is also where ownership of the client stops.
/// <see cref="DelegatingChatClient"/> disposes what it wraps and the tool path
/// builds a <c>FunctionInvokingChatClient</c> per call, so without this the
/// runner disposes a client it does not own and the second call through a
/// shared role fails.
/// </para>
/// </remarks>
internal sealed class CountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentScope.RecordRequest();

        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        Record(response.Usage, AgentScope.Current, UsageAccumulator.Current);
        return response;
    }

    /// <summary>
    /// Not an async iterator, so the budget check stays eager.
    /// </summary>
    /// <remarks>
    /// An <c>async IAsyncEnumerable</c> body does not run until somebody calls
    /// <c>MoveNextAsync</c>, which would move
    /// <see cref="AgentScope.RecordRequest"/> from "when the call was made" to
    /// "when the caller got round to reading it". Nothing is sent before then
    /// either, so the bound would still hold — but a budget that throws from a
    /// <c>foreach</c> rather than from the call it refuses is a worse thing to
    /// read in a stack trace.
    /// </remarks>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentScope.RecordRequest();

        // Both are AsyncLocal, and usage arrives during enumeration rather than
        // here. Captured now so an enumerable read from another execution
        // context bills the scope that asked for it, not whichever one happened
        // to be current when the tokens showed up.
        return Observe(
            base.GetStreamingResponseAsync(messages, options, cancellationToken),
            AgentScope.Current,
            UsageAccumulator.Current,
            cancellationToken);
    }

    /// <summary>Passes updates through, keeping the usage ones as they go by.</summary>
    /// <remarks>
    /// A streaming response reports usage as a <see cref="UsageContent"/> in an
    /// update's contents, generally the last one, and generally only when the
    /// provider was asked for it.
    /// </remarks>
    private static async IAsyncEnumerable<ChatResponseUpdate> Observe(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        AgentScope? scope,
        UsageAccumulator? accumulator,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in updates.WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage) Record(usage.Details, scope, accumulator);
            }

            yield return update;
        }
    }

    /// <summary>
    /// Bills one request's usage to the workflow and to the method call.
    /// </summary>
    /// <remarks>
    /// Both, because they answer different questions and neither derives the
    /// other: the scope wants what a composition cost end to end, and the
    /// accumulator wants which method spent it.
    /// </remarks>
    private static void Record(UsageDetails? usage, AgentScope? scope, UsageAccumulator? accumulator)
    {
        if (usage is null) return;

        scope?.AddUsage(usage);
        accumulator?.Add(usage);
    }

    /// <summary>Disposes nothing. The client belongs to whoever built it.</summary>
    /// <remarks>
    /// A runner is handed an <see cref="IChatClient"/>; it does not create one
    /// and must not close one. Swallowing the call here rather than declining
    /// to dispose the <c>FunctionInvokingChatClient</c> puts the rule at the
    /// boundary it belongs to, so it holds for every wrapper the tool path
    /// builds now or later.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        // Deliberately not base.Dispose(disposing).
    }
}
