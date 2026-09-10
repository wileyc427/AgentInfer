using System.Text.Json;

using Microsoft.Extensions.AI;

using Xunit;

namespace Agentry.Tests;

/// <summary>
/// What the runtime actually sends, and what it does with what comes back.
/// </summary>
public sealed class AgentRunnerTests
{
    /// <summary>xUnit v3 wants every awaited call to carry the test's token, so
    /// a hung call fails the test instead of the run.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCall Call(string task = "Do the thing.", params (string Name, string Value)[] args) =>
        new()
        {
            SystemPrompt = "You are terse.",
            TaskPrompt = task,
            Operation = "IThing.DoAsync",
            Arguments = [.. args.Select(a => new KeyValuePair<string, string>(a.Name, a.Value))],
        };

    [Fact]
    public async Task The_system_prompt_is_a_system_message()
    {
        var client = new FakeChatClient("ok");
        await new AgentRunner(client).CompleteTextAsync(Call(), Ct);

        Assert.Equal(ChatRole.System, client.Received![0].Role);
        Assert.Equal("You are terse.", client.Received[0].Text);
    }

    [Fact]
    public async Task Arguments_are_delimited_by_name()
    {
        var client = new FakeChatClient("ok");
        await new AgentRunner(client).CompleteTextAsync(Call("Summarise it.", ("spending", "coffee: 22.80")), Ct);

        var user = client.Received![1].Text;
        Assert.Contains("Summarise it.", user);
        // Tagged rather than concatenated: a model given three bare paragraphs
        // has to guess which is which, and guesses wrong on the long ones.
        Assert.Contains("<spending>\ncoffee: 22.80\n</spending>", user);
    }

    [Fact]
    public async Task A_text_return_is_handed_back_untouched()
    {
        var runner = new AgentRunner(new FakeChatClient("  You spent 22.80.  "));
        Assert.Equal("  You spent 22.80.  ", await runner.CompleteTextAsync(Call(), Ct));
    }

    [Fact]
    public async Task A_text_return_asks_for_no_json()
    {
        var client = new FakeChatClient("ok");
        await new AgentRunner(client).CompleteTextAsync(Call(), Ct);

        // Asking a model to wrap prose in JSON so it can be unwrapped again is
        // a round trip's worth of ways to fail, for nothing.
        Assert.DoesNotContain("JSON", client.Received![1].Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Verdict(bool Approved, int Score);

    [Fact]
    public async Task A_typed_return_is_bound_from_json()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"approved":true,"score":4}"""));
        var verdict = await runner.CompleteJsonAsync<Verdict>(Call(), ct: Ct);

        Assert.True(verdict.Approved);
        Assert.Equal(4, verdict.Score);
    }

    [Fact]
    public async Task A_markdown_fence_is_stripped()
    {
        // Small local models add one regardless of what they were asked for.
        var runner = new AgentRunner(new FakeChatClient("```json\n{\"approved\":false,\"score\":2}\n```"));
        Assert.False((await runner.CompleteJsonAsync<Verdict>(Call(), ct: Ct)).Approved);
    }

    [Fact]
    public async Task A_reply_that_will_not_bind_says_what_the_model_actually_said()
    {
        var runner = new AgentRunner(new FakeChatClient("I think it looks fine, honestly."));

        var error = await Assert.ThrowsAsync<AgentException>(() => runner.CompleteJsonAsync<Verdict>(Call(), ct: Ct));

        // The thing you need when this fires is the reply, not a stack trace.
        Assert.Contains("I think it looks fine", error.Message);
        Assert.Contains("Verdict", error.Message);
        Assert.Equal("IThing.DoAsync", error.Operation);
    }

    [Fact]
    public async Task Json_null_is_an_error_rather_than_a_null_reference_later()
    {
        var runner = new AgentRunner(new FakeChatClient("null"));
        await Assert.ThrowsAsync<AgentException>(() => runner.CompleteJsonAsync<Verdict>(Call(), ct: Ct));
    }

    [Fact]
    public async Task Cancellation_flows_through()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var runner = new AgentRunner(new CancellingClient());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.CompleteTextAsync(Call(), cts.Token));
    }

    private sealed class CancellingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
