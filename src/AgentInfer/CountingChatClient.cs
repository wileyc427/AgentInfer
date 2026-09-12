using Microsoft.Extensions.AI;

namespace AgentInfer;

/// <summary>
/// Counts every request against the enclosing <see cref="AgentScope"/>, and
/// refuses the one that would exceed its bound.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DelegatingChatClient"/> rather than a check inside
/// <see cref="AgentRunner"/>, for the reason the permission gate lives inside
/// <c>GatedFunction</c>: this is the only place every request is visible. A
/// tool-using method is one call to the runner and one request per round of
/// tool calls, and those rounds belong to <c>FunctionInvokingChatClient</c>,
/// which does not report them. Counting where the runner can see is counting
/// the cheap half.
/// </para>
/// <para>
/// <b>Applied by the runner to its own client, not registered by a host.</b>
/// The obvious alternative is an <c>AddAgentInferBudget()</c> a consumer wires
/// into their <c>IChatClient</c> pipeline, which is more idiomatic and has one
/// fatal property: forget it and <c>AgentScope.Begin("x", maxRequests: 20)</c>
/// compiles, reads correctly, and does nothing. A bound that silently is not
/// one is worse than no bound at all.
/// </para>
/// <para>
/// Stateless — the counts live on the ambient scope — so one instance per
/// runner is enough and it costs an allocation at construction, not per call.
/// </para>
/// <para>
/// <b>It is also where ownership of the client stops.</b>
/// <see cref="DelegatingChatClient"/> disposes what it wraps, and the tool path
/// builds a <c>FunctionInvokingChatClient</c> in a <c>using</c> per call — so
/// the runner was disposing a client it did not own, every tool-using call.
/// Nothing caught it: the sample uses a different client for each of its two
/// calls, and a fake with a no-op <c>Dispose</c> cannot tell. On a real host,
/// where <c>AddAgentInferModels</c> registers one client per role and shares it,
/// the second call through the same role fails.
/// </para>
/// </remarks>
internal sealed class CountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentScope.RecordRequest();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentScope.RecordRequest();
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
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
