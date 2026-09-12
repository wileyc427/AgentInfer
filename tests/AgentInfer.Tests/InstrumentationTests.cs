using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Xunit;

namespace AgentInfer.Tests;

/// <summary>
/// The counts that decide whether this workload ever needs generated code.
/// </summary>
/// <remarks>
/// Composing tool calls in code the model writes buys one thing — fewer round
/// trips — at the cost of running that code. Obviously worth it at fifteen
/// calls a turn, obviously not at two. These make the number observable so the
/// decision is data rather than taste.
/// </remarks>
public sealed class InstrumentationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Ledger : ToolInvoker
    {
        public override ToolManifest Manifest { get; } = new(
        [
            new("TotalFor", "Reads.", """{"type":"object","properties":{}}""", ["ledger.read"]),
            new("Reclassify", "Writes.", """{"type":"object","properties":{}}""", ["ledger.write"]),
        ]);

        protected override Task<string> DispatchAsync(string name, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult("22.80");
    }

    /// <summary>Asks for the same tool <paramref name="calls"/> times, then answers.</summary>
    private sealed class Chatty(int calls, string tool = "TotalFor") : IChatClient
    {
        private int _made;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            if (_made < calls)
            {
                _made++;
                var call = new FunctionCallContent($"c{_made}", tool, new Dictionary<string, object?>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Captures formatted log lines, so the summary is asserted as read.</summary>
    private sealed class Capturing : ILogger<AgentRunner>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static AgentCall Call() => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = "Total everything.",
        Operation = "ILedger.SummarizeAsync",
        Arguments = [],
    };

    [Fact]
    public async Task The_summary_says_how_many_calls_the_turn_actually_made()
    {
        var logger = new Capturing();

        await new AgentRunner(new Chatty(4), logger).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), maxIterations: 10, ct: Ct);

        var summary = logger.Lines.Single(l => l.Contains("tools offered"));

        // The number that decides P3, in a line somebody reads while developing.
        Assert.Contains("1 of 2 tools offered", summary);
        Assert.Contains("4 call(s)", summary);
        Assert.Contains("TotalFor×4", summary);
    }

    [Fact]
    public async Task A_turn_that_uses_no_tools_says_so_rather_than_going_quiet()
    {
        var logger = new Capturing();

        await new AgentRunner(new Chatty(0), logger).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), ct: Ct);

        var summary = logger.Lines.Single(l => l.Contains("tools offered"));

        // "Zero" is the answer that most strongly argues against generated code,
        // so it has to be visible rather than an absent line.
        Assert.Contains("0 call(s)", summary);
        Assert.Contains("no tool calls", summary);
    }

    [Fact]
    public async Task Asking_for_a_tool_that_was_never_offered_reaches_no_tool_at_all()
    {
        var logger = new Capturing();

        await new AgentRunner(new Chatty(2, "Reclassify"), logger).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), ct: Ct);

        var summary = logger.Lines.Single(l => l.Contains("tools offered"));

        // Zero, not "2 denied" — and the difference is worth understanding.
        // Reclassify was filtered out of ChatOptions.Tools, so there is no
        // AIFunction with that name for the loop to invoke; it never reaches
        // GatedFunction and the second gate never fires.
        //
        // Which means: within one call, filtering the menu is what enforces the
        // permission, and the check inside GatedFunction is belt and braces.
        // The check earns its place across calls — a long-lived invoker whose
        // authorizer changes between turns, or a consumer calling InvokeAsync
        // directly — not here.
        Assert.Contains("0 call(s)", summary);
    }

    [Fact]
    public async Task Denials_are_counted_separately_when_the_gate_does_fire()
    {
        var invoker = new Ledger();
        var log = new ToolCallLog { Offered = 1, Total = 2 };

        // The path the loop cannot take: someone holding the invoker calls a
        // tool the caller may not use. This is the case the second gate exists
        // for, and it is a denial rather than a call.
        await Assert.ThrowsAsync<ToolDeniedException>(
            () => invoker.InvokeAsync("Reclassify", "{}", new GrantedPermissions(["ledger.read"]), Ct));

        log.Record("Reclassify", denied: true);

        Assert.Equal(1, log.Invocations);
        Assert.Equal(1, log.Denials);
    }

    [Fact]
    public void The_breakdown_orders_by_frequency_so_a_hot_tool_is_visible()
    {
        var log = new ToolCallLog { Offered = 2, Total = 2 };

        log.Record("Rare", denied: false);
        for (var i = 0; i < 5; i++) log.Record("Hot", denied: false);

        Assert.Equal("Hot×5, Rare×1", log.ToString());
        Assert.Equal(6, log.Invocations);
        Assert.Equal(0, log.Denials);
    }
}
