using Microsoft.Extensions.AI;

using Xunit;

namespace AgentInfer.Tests;

/// <summary>
/// What a workflow cost, and the bound that stops it costing more.
/// </summary>
/// <remarks>
/// Composition is plain C# and needs no framework surface, which leaves nowhere
/// to hang a number: several generation calls composed with <c>await</c> emit
/// unrelated spans, and <c>MaxIterations</c> bounds one method's tool loop
/// rather than the whole.
/// </remarks>
public sealed class AgentScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCall Call(string operation = "IThing.DoAsync") => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = "Do the thing.",
        Operation = operation,
        Arguments = [],
    };

    [Fact]
    public async Task A_scope_counts_the_operations_composed_inside_it()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        using var scope = AgentScope.Begin("triage");

        await runner.CompleteTextAsync(Call(), Ct);
        await runner.CompleteTextAsync(Call(), Ct);
        await runner.CompleteTextAsync(Call(), Ct);

        Assert.Equal(3, scope.Operations);
        Assert.Equal(3, scope.Requests);
    }

    [Fact]
    public async Task A_typed_tool_using_method_is_one_operation_and_two_requests()
    {
        var runner = new AgentRunner(new FakeChatClient("{\"approved\":true}"));
        using var scope = AgentScope.Begin("review");

        await runner.CompleteJsonWithToolsReflectivelyAsync<Approval>(
            Call(), new NoTools(), GrantAllTools.Instance, ct: Ct);

        // The two-phase path is the reason both numbers exist: the loop runs
        // with no JSON instruction, then a second call binds with no tools.
        Assert.Equal(1, scope.Operations);
        Assert.Equal(2, scope.Requests);
    }

    [Fact]
    public async Task Nothing_changes_outside_a_scope()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        Assert.Null(AgentScope.Current);
        Assert.Equal("ok", await runner.CompleteTextAsync(Call(), Ct));
    }

    [Fact]
    public async Task Counts_roll_up_to_an_enclosing_scope()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        using var outer = AgentScope.Begin("incident");
        await runner.CompleteTextAsync(Call(), Ct);

        using (var inner = AgentScope.Begin("incident.investigate"))
        {
            await runner.CompleteTextAsync(Call(), Ct);
            await runner.CompleteTextAsync(Call(), Ct);
            Assert.Equal(2, inner.Operations);
        }

        // An agent reached as another agent's tool works in a nested scope, and
        // the outer number has to include it — otherwise the cheapest-looking
        // orchestration is the one that hides its calls one level down.
        Assert.Equal(3, outer.Operations);
    }

    [Fact]
    public async Task Closing_a_nested_scope_restores_the_outer_one()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        using var outer = AgentScope.Begin("incident");
        using (AgentScope.Begin("incident.investigate")) { }

        Assert.Same(outer, AgentScope.Current);

        await runner.CompleteTextAsync(Call(), Ct);
        Assert.Equal(1, outer.Operations);
    }

    [Fact]
    public async Task A_fanned_out_scope_counts_every_branch()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        using var scope = AgentScope.Begin("sections");

        // Sectioning is Task.WhenAll and needs no framework support — but the
        // counters have to survive it, so they are interlocked.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => runner.CompleteTextAsync(Call(), Ct)));

        Assert.Equal(16, scope.Operations);
    }

    [Fact]
    public async Task A_bound_stops_the_workflow_rather_than_truncating_it()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        using var scope = AgentScope.Begin("triage", maxRequests: 2);

        await runner.CompleteTextAsync(Call(), Ct);
        await runner.CompleteTextAsync(Call(), Ct);

        var error = await Assert.ThrowsAsync<AgentBudgetExceededException>(
            () => runner.CompleteTextAsync(Call(), Ct));

        // Throws rather than truncating: an answer written from a partial
        // gather reads fine and is false.
        Assert.Equal("triage", error.Workflow);
        Assert.Equal(2, error.MaxRequests);
    }

    [Fact]
    public async Task The_innermost_bound_is_the_one_that_fires()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        using var outer = AgentScope.Begin("incident", maxRequests: 100);
        using var inner = AgentScope.Begin("incident.investigate", maxRequests: 1);

        await runner.CompleteTextAsync(Call(), Ct);

        var error = await Assert.ThrowsAsync<AgentBudgetExceededException>(
            () => runner.CompleteTextAsync(Call(), Ct));

        Assert.Equal("incident.investigate", error.Workflow);
    }

    [Fact]
    public async Task A_bound_counts_tool_rounds_not_just_operations()
    {
        // Two tool rounds plus the final answer is three requests from one
        // operation. Bounding operations would have missed the expensive half.
        var client = new ToolCallingClient(rounds: 2);
        var runner = new AgentRunner(client);

        using var scope = AgentScope.Begin("chatty", maxRequests: 2);

        await Assert.ThrowsAnyAsync<Exception>(
            () => runner.CompleteWithToolsAsync(Call(), new OneTool(), GrantAllTools.Instance, ct: Ct));

        Assert.Equal(2, scope.Requests);
    }

    [Fact]
    public async Task The_runner_does_not_dispose_a_client_it_was_handed()
    {
        var client = new CountingDisposals();
        var runner = new AgentRunner(client);

        // FunctionInvokingChatClient is built in a `using` per call, and
        // DelegatingChatClient disposes what it wraps — so every tool-using
        // call was closing the caller's client. On a host where one client is
        // shared across roles, the second call through it fails.
        await runner.CompleteWithToolsAsync(Call(), new NoTools(), GrantAllTools.Instance, ct: Ct);
        await runner.CompleteWithToolsAsync(Call(), new NoTools(), GrantAllTools.Instance, ct: Ct);

        Assert.Equal(0, client.Disposals);
    }

    public sealed record Approval(bool Approved);

    private sealed class NoTools : ToolInvoker
    {
        public override ToolManifest Manifest => ToolManifest.Empty;

        protected override Task<string> DispatchAsync(
            string name, System.Text.Json.JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(string.Empty);
    }

    private sealed class OneTool : ToolInvoker
    {
        public override ToolManifest Manifest { get; } = new(
        [
            new("Ping", "Pings.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}", []),
        ]);

        protected override Task<string> DispatchAsync(
            string name, System.Text.Json.JsonElement arguments, CancellationToken ct) =>
            Task.FromResult("pong");
    }

    /// <summary>Asks for a tool the first <c>rounds</c> times, then answers.</summary>
    private sealed class ToolCallingClient(int rounds) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var message = _calls++ < rounds
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{_calls}", "Ping", null)])
                : new ChatMessage(ChatRole.Assistant, "done");

            return Task.FromResult(new ChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CountingDisposals : IChatClient
    {
        public int Disposals { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposals++;
    }
    /// <summary>
    /// The shape a web host produces: many independent scopes at once over one
    /// shared runner and client, rather than one scope fanning out.
    /// </summary>
    [Fact]
    public async Task Concurrent_scopes_count_only_their_own_traffic()
    {
        // One client and one runner, as AddAgentInferModels registers per role.
        var runner = new AgentRunner(new FakeChatClient("ok"));

        const int Scopes = 24;
        const int CallsEach = 5;

        var results = await Task.WhenAll(Enumerable.Range(0, Scopes).Select(i => Task.Run(async () =>
        {
            using var scope = AgentScope.Begin($"request-{i}");
            for (var c = 0; c < CallsEach; c++)
            {
                await runner.CompleteTextAsync(Call(), Ct);
            }

            return (scope.Name, scope.Requests, scope.Operations);
        }, Ct)));

        Assert.All(results, r =>
        {
            Assert.Equal(CallsEach, r.Requests);
            Assert.Equal(CallsEach, r.Operations);
        });
        Assert.Equal(Scopes, results.Select(r => r.Name).Distinct().Count());
    }

    /// <summary>One scope's traffic must not spend another scope's budget.</summary>
    [Fact]
    public async Task A_bound_is_not_consumed_by_a_concurrent_scope()
    {
        var runner = new AgentRunner(new FakeChatClient("ok"));

        // One scope deliberately burns far more than another's bound allows.
        var noisy = Task.Run(async () =>
        {
            using var scope = AgentScope.Begin("noisy");
            for (var i = 0; i < 40; i++) await runner.CompleteTextAsync(Call(), Ct);
        }, Ct);

        var quiet = Task.Run(async () =>
        {
            using var scope = AgentScope.Begin("quiet", maxRequests: 3);
            for (var i = 0; i < 3; i++) await runner.CompleteTextAsync(Call(), Ct);
            return scope.Requests;
        }, Ct);

        await noisy;
        Assert.Equal(3, await quiet);
    }
}
