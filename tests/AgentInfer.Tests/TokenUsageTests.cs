using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Xunit;

namespace AgentInfer.Tests;

/// <summary>
/// What a call cost, which requests cannot tell you.
/// </summary>
/// <remarks>
/// <see cref="AgentScope.Requests"/> treats every request as interchangeable,
/// and the first thing anyone builds with tools is a step whose prompt grows by
/// a tool result each round. These pin the number that does not.
/// </remarks>
public sealed class TokenUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reports the same usage on every request.</summary>
    private sealed class Metered(long input, long output, int toolCalls = 0, long reasoning = 0, string reply = "Done.") : IChatClient
    {
        private int _made;

        public int Requests { get; private set; }

        private UsageDetails Usage() => new()
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            ReasoningTokenCount = reasoning > 0 ? reasoning : null,
        };

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests++;

            var message = _made++ < toolCalls
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{_made}", "TotalFor", new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, reply);

            return Task.FromResult(new ChatResponse(message) { Usage = Usage() });
        }

        /// <summary>Reports usage the way a provider does: in a trailing update.</summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests++;

            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.Yield();

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(Usage())]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A provider that reports no usage at all, which many do.</summary>
    private sealed class Silent : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class Ledger : ToolInvoker
    {
        public override ToolManifest Manifest { get; } = new(
        [
            new("TotalFor", "Reads.", """{"type":"object","properties":{}}""", ["ledger.read"]),
        ]);

        protected override Task<string> DispatchAsync(string name, System.Text.Json.JsonElement arguments, CancellationToken ct) =>
            Task.FromResult("22.80");
    }

    private sealed class Capturing : ILogger<AgentRunner>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static AgentCall Call(string operation = "ILedger.SummarizeAsync") => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = "Total everything.",
        Operation = operation,
        Arguments = [],
    };

    /// <summary>
    /// Collects one instrument's measurements, keeping only those tagged with
    /// <paramref name="operation"/>.
    /// </summary>
    /// <remarks>
    /// The meter is process-wide and the test class runs beside others, so
    /// filtering on a tag the caller made unique is what keeps a parallel test's
    /// measurements out of this one's list.
    /// </remarks>
    private static (MeterListener Listener, List<long> Values) Collect(string instrument, string operation)
    {
        var values = new List<long>();

        var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == AgentMetrics.MeterName && i.Name == instrument) l.EnableMeasurementEvents(i);
            },
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "operation" && (string?)tag.Value == operation)
                {
                    lock (values) values.Add(measurement);
                    return;
                }
            }
        });

        listener.Start();
        return (listener, values);
    }

    [Fact]
    public async Task A_call_is_billed_every_request_it_made_not_just_its_last()
    {
        var client = new Metered(input: 100, output: 20, toolCalls: 3);
        using var scope = AgentScope.Begin("ledger.total");

        await new AgentRunner(client).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), maxIterations: 10, ct: Ct);

        // Three rounds asking for a tool, then the answer. The tool loop owns
        // those rounds and does not report them, which is the whole reason the
        // counting client sits inside it rather than around it.
        Assert.Equal(4, client.Requests);
        Assert.Equal(400, scope.Tokens.Input);
        Assert.Equal(80, scope.Tokens.Output);
    }

    [Fact]
    public async Task A_scope_sums_what_every_operation_in_it_spent()
    {
        var runner = new AgentRunner(new Metered(input: 100, output: 20));

        using var scope = AgentScope.Begin("ledger.review");

        await runner.CompleteTextAsync(Call("ILedger.SummarizeAsync"), Ct);
        await runner.CompleteTextAsync(Call("ILedger.ReviewAsync"), Ct);

        Assert.Equal(2, scope.Operations);
        Assert.Equal(200, scope.Tokens.Input);
        Assert.Equal(240, scope.Tokens.Total);
    }

    [Fact]
    public async Task An_agent_used_as_another_agents_tool_cannot_spend_unseen()
    {
        var runner = new AgentRunner(new Metered(input: 100, output: 20));

        using var outer = AgentScope.Begin("incident.triage");
        await runner.CompleteTextAsync(Call(), Ct);

        using (var inner = AgentScope.Begin("incident.investigate"))
        {
            await runner.CompleteTextAsync(Call(), Ct);
            Assert.Equal(100, inner.Tokens.Input);
        }

        // The point of the rollup: a nested workflow's bill lands on the
        // enclosing one too, so "what did that whole thing cost" has an answer.
        Assert.Equal(200, outer.Tokens.Input);
    }

    [Fact]
    public async Task Streaming_usage_arrives_in_an_update_and_is_still_collected()
    {
        var runner = new AgentRunner(new Metered(input: 100, output: 20));

        using var scope = AgentScope.Begin("ledger.stream");

        await foreach (var _ in runner.StreamTextAsync(Call(), Ct)) { }

        // A streaming response reports usage as a UsageContent on a trailing
        // update, not on a ChatResponse. Reading only the non-streaming path
        // would leave every streamed call looking free.
        Assert.True(scope.UsageReported);
        Assert.Equal(100, scope.Tokens.Input);
        Assert.Equal(20, scope.Tokens.Output);
    }

    [Fact]
    public async Task A_provider_that_reports_nothing_is_not_reported_as_free()
    {
        var logger = new Capturing();

        using var scope = AgentScope.Begin("ledger.unmeasured");
        await new AgentRunner(new Silent(), logger).CompleteTextAsync(Call(), Ct);

        // Zero tokens and no tokens are different facts, and a dashboard that
        // renders them the same will be read as "this workload is cheap".
        Assert.False(scope.UsageReported);
        Assert.DoesNotContain("token", scope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_log_line_says_what_the_call_spent()
    {
        var logger = new Capturing();

        await new AgentRunner(new Metered(input: 100, output: 20), logger).CompleteTextAsync(Call(), Ct);

        Assert.Contains(logger.Lines, l => l.Contains("100 in / 20 out token(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tokens_are_recorded_against_the_method_that_spent_them()
    {
        var operation = $"ILedger.{nameof(Tokens_are_recorded_against_the_method_that_spent_them)}";

        var (listener, input) = Collect("agentinfer.tokens.input", operation);
        using (listener)
        {
            await new AgentRunner(new Metered(input: 100, output: 20, toolCalls: 2)).CompleteWithToolsAsync(
                Call(operation), new Ledger(), new GrantedPermissions(["ledger.read"]), maxIterations: 10, ct: Ct);
        }

        // One measurement per method call, not per request — the histogram's
        // unit is "what one call costs", which is the thing a budget is set in.
        Assert.Equal([300], input);
    }

    [Fact]
    public async Task A_two_phase_call_is_billed_its_binding_request_too()
    {
        var operation = $"ILedger.{nameof(A_two_phase_call_is_billed_its_binding_request_too)}";

        var client = new Metered(input: 100, output: 1, toolCalls: 1, reply: """{"value":3,"reason":"ok"}""");
        using var scope = AgentScope.Begin("ledger.bind");

        var (listener, input) = Collect("agentinfer.tokens.input", operation);
        using (listener)
        {
            await new AgentRunner(client).CompleteJsonWithToolsAsync(
                Call(operation), ScoreContract.Instance, new Ledger(), new GrantedPermissions(["ledger.read"]),
                maxIterations: 10, ct: Ct);
        }

        // A tool-using typed method runs in two phases: the loop, then a
        // separate request that binds with no tools offered. Three requests —
        // one round asking for the tool, one answering it, one binding.
        Assert.Equal(3, client.Requests);

        // The per-operation histogram has to agree with the scope, and it did
        // not: the tool loop reported at its own end, which is the operation's
        // end for exactly one of that helper's four callers. This method was
        // billed 200 of the 300 it spent, and the missing request was the
        // largest — the binding prompt carries the whole tool-loop answer.
        Assert.Equal(300, scope.Tokens.Input);
        Assert.Equal([300], input);
    }

    [Fact]
    public async Task Reasoning_tokens_are_kept_apart_from_the_reply()
    {
        var operation = $"ILedger.{nameof(Reasoning_tokens_are_kept_apart_from_the_reply)}";

        var (listener, reasoning) = Collect("agentinfer.tokens.reasoning", operation);
        using (listener)
        {
            await new AgentRunner(new Metered(input: 100, output: 500, reasoning: 480))
                .CompleteTextAsync(Call(operation), Ct);
        }

        // The number that explains a forty-second call returning a hundred
        // characters. Folded into output it is invisible.
        Assert.Equal([480], reasoning);
    }
}
