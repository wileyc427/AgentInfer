using Microsoft.Extensions.AI;

namespace AgentInfer.Tests;

/// <summary>
/// An <see cref="IChatClient"/> that returns a canned reply and records what it
/// was asked.
/// </summary>
/// <remarks>
/// The whole runtime is testable this way because it takes an
/// <see cref="IChatClient"/> and nothing else — no HTTP, no provider SDK, no
/// key. That is the payoff for not writing a chat abstraction of our own: the
/// platform's interface is the seam, so a fake is nine lines.
/// </remarks>
internal sealed class FakeChatClient(string reply) : IChatClient
{
    public IList<ChatMessage>? Received { get; private set; }

    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Received = [.. messages];
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    /// <summary>The same reply, in pieces.</summary>
    /// <remarks>
    /// Chunked rather than handed over whole, because a fake that yields one
    /// update would pass a streaming test that a non-streaming implementation
    /// also passes. Four characters at a time is arbitrary and small enough
    /// that every reply used here produces several.
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        Received = [.. messages];
        LastOptions = options;

        for (var at = 0; at < reply.Length; at += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var piece = reply.Substring(at, Math.Min(4, reply.Length - at));
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece);

            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
