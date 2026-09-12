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

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Streaming is not part of P1.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
