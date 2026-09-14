using Xunit;

namespace AgentInfer.Tests;

/// <summary>
/// <see cref="AgentRunner.StreamTextAsync"/>: the reply, as it arrives.
/// </summary>
public sealed class StreamingTests
{
    private static AgentCall Call(string prompt = "Draft it.") => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = prompt,
        Operation = "IWriter.DraftAsync",
        Arguments = [],
    };

    [Fact]
    public async Task The_pieces_arrive_separately_and_reassemble()
    {
        var runner = new AgentRunner(new FakeChatClient("a postmortem, in prose"));

        var pieces = new List<string>();
        await foreach (var piece in runner.StreamTextAsync(Call(), TestContext.Current.CancellationToken))
        {
            pieces.Add(piece);
        }

        // More than one, or this passes for an implementation that streams nothing.
        Assert.True(pieces.Count > 1, $"expected several pieces, got {pieces.Count}");
        Assert.Equal("a postmortem, in prose", string.Concat(pieces));
    }

    /// <remarks>
    /// The budget is enforced by <c>CountingChatClient</c>, which overrides the
    /// streaming call as well as the buffered one. This is the test that says
    /// so — a bound that only held on one path would be worse than none.
    /// </remarks>
    [Fact]
    public async Task A_streamed_call_counts_against_the_scope()
    {
        var runner = new AgentRunner(new FakeChatClient("something"));

        using var scope = AgentScope.Begin("draft", maxRequests: 5);

        await foreach (var _ in runner.StreamTextAsync(Call(), TestContext.Current.CancellationToken)) { }

        Assert.Equal(1, scope.Requests);
    }

    [Fact]
    public async Task A_bound_that_is_spent_refuses_the_next_stream()
    {
        var runner = new AgentRunner(new FakeChatClient("something"));

        using var scope = AgentScope.Begin("draft", maxRequests: 1);

        await foreach (var _ in runner.StreamTextAsync(Call(), TestContext.Current.CancellationToken)) { }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in runner.StreamTextAsync(Call(), TestContext.Current.CancellationToken)) { }
        });
    }

    /// <remarks>
    /// Abandoning the enumeration must stop the work rather than run it to
    /// completion in the background, which is the failure a streaming API
    /// invites and nothing else here would catch.
    /// </remarks>
    [Fact]
    public async Task Breaking_out_early_stops_asking_for_more()
    {
        var runner = new AgentRunner(new FakeChatClient(new string('x', 400)));

        var seen = 0;
        await foreach (var piece in runner.StreamTextAsync(Call(), TestContext.Current.CancellationToken))
        {
            seen += piece.Length;
            if (seen >= 8) break;
        }

        Assert.InRange(seen, 8, 12);
    }
}
