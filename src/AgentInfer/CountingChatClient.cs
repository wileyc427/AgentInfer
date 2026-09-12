using Microsoft.Extensions.AI;

namespace AgentInfer;

/// <summary>
/// Counts every request against the enclosing <see cref="AgentScope"/>, and
/// refuses the one that would exceed its bound.
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
