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
    public async Task The_binding_call_still_carries_the_original_arguments()
    {
        var client = new RecordingClient();
        var runner = new AgentRunner(client);

        var call = new AgentCall
        {
            SystemPrompt = "You are terse.",
            TaskPrompt = "Investigate this service.",
            Operation = "IInvestigator.InvestigateAsync",
            Arguments = [new KeyValuePair<string, string>("service", "payments")],
        };

        await runner.CompleteJsonWithToolsAsync<Health>(call, new NoTools(), GrantAllTools.Instance, ct: Ct);

        // The second phase is a fresh two-message request, and it used to get
        // only <answer>. A method taking a service name and returning a record
        // with a Service field was then asked to produce one from the answer
        // text alone — and a model that could not find it there invented one.
        var binding = client.Requests[^1][1].Text;

        Assert.Contains("<service>\npayments\n</service>", binding);
        Assert.Contains("<answer>", binding);
    }

    public sealed record Health(string Service, bool Healthy);

    private sealed class NoTools : ToolInvoker
    {
        public override ToolManifest Manifest => ToolManifest.Empty;

        protected override Task<string> DispatchAsync(
            string name, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(string.Empty);
    }

    private sealed class RecordingClient : IChatClient
    {
        public List<IList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "{\"service\":\"payments\",\"healthy\":false}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

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
        await new AgentRunner(client).CompleteTextAsync(Call("Summarize it.", ("spending", "coffee: 22.80")), Ct);

        var user = client.Received![1].Text;
        Assert.Contains("Summarize it.", user);
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

    private sealed record Report(bool Approved, int Score, string[] Problems);

    private const string ReportSchema =
        """{"type":"object","properties":{"approved":{"type":"boolean"}},"required":["approved"],"additionalProperties":false}""";

    private sealed record Scored(bool Approved, [property: System.ComponentModel.DataAnnotations.Range(1, 5)] int Score);

    [Fact]
    public async Task A_value_outside_its_declared_range_is_refused_after_binding()
    {
        // The shape was right and it bound. A real run answered score: 100 out
        // of five and nothing objected, because 100 is a perfectly good integer.
        var runner = new AgentRunner(new FakeChatClient("""{"approved":true,"score":100}"""));

        var error = await Assert.ThrowsAsync<AgentException>(
            () => runner.CompleteJsonAsync<Scored>(Call(), ct: Ct));

        Assert.Contains("failed validation", error.Message);
        Assert.Contains("Score", error.Message);
        // The reply is quoted, for the same reason the bind failure quotes it.
        Assert.Contains("100", error.Message);
    }

    [Fact]
    public async Task A_value_inside_its_range_still_binds()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"approved":true,"score":4}"""));
        Assert.Equal(4, (await runner.CompleteJsonAsync<Scored>(Call(), ct: Ct)).Score);
    }

    [Fact]
    public async Task The_model_is_told_the_shape_not_only_the_format()
    {
        var client = new FakeChatClient("""{"approved":true,"score":4,"problems":[]}""");

        await new AgentRunner(client).CompleteJsonAsync<Report>(
            Call() with { ResponseSchema = ReportSchema }, ct: Ct);

        // "Reply with JSON" alone left a model guessing which JSON; a real run
        // answered {"supported": true} to a three-field record.
        Assert.Contains(ReportSchema, client.Received![1].Text);
    }

    [Fact]
    public async Task The_provider_is_asked_to_enforce_the_shape_as_well()
    {
        var client = new FakeChatClient("""{"approved":true,"score":4,"problems":[]}""");

        await new AgentRunner(client).CompleteJsonAsync<Report>(
            Call() with { ResponseSchema = ReportSchema }, ct: Ct);

        // Both, because they fail in different places: a provider that ignores
        // response_format still sees the prompt, and a model that ignores the
        // prompt is still constrained by the provider.
        Assert.IsType<Microsoft.Extensions.AI.ChatResponseFormatJson>(client.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task A_text_call_asks_the_provider_for_no_particular_format()
    {
        var client = new FakeChatClient("fine");
        await new AgentRunner(client).CompleteTextAsync(Call(), Ct);

        Assert.Null(client.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task A_missing_property_is_an_error_at_the_boundary_not_a_null_three_frames_later()
    {
        // This exact reply, to this exact record, produced a Report with null in
        // the non-nullable Problems slot — and a NullReferenceException in the
        // caller's foreach, several lines from the cause. Task<Report> has to
        // mean a Report.
        var runner = new AgentRunner(new FakeChatClient("""{"approved":false,"score":0}"""));

        var error = await Assert.ThrowsAsync<AgentException>(
            () => runner.CompleteJsonAsync<Report>(Call(), ct: Ct));

        Assert.Contains("problems", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_explicit_null_in_a_non_nullable_slot_is_refused_too()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"approved":true,"score":4,"problems":null}"""));
        await Assert.ThrowsAsync<AgentException>(() => runner.CompleteJsonAsync<Report>(Call(), ct: Ct));
    }

    [Fact]
    public async Task A_complete_reply_still_binds()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"approved":true,"score":4,"problems":[]}"""));
        var report = await runner.CompleteJsonAsync<Report>(Call(), ct: Ct);

        Assert.True(report.Approved);
        Assert.Empty(report.Problems);
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

        var runner = new AgentRunner(new CancelingClient());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.CompleteTextAsync(Call(), cts.Token));
    }

    private sealed class CancelingClient : IChatClient
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
