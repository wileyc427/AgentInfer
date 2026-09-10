using System.Text.Json;

using Microsoft.Extensions.AI;

using Xunit;

namespace Agentry.Tests;

/// <summary>
/// The tool-calling loop, driven end to end against a scripted client.
/// </summary>
public sealed class ToolLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Ledger : ToolInvoker
    {
        public List<string> Calls { get; } = [];

        public override ToolManifest Manifest { get; } = new(
        [
            new("TotalFor", "The total for one category.",
                """{"type":"object","properties":{"category":{"type":"string"}},"required":["category"]}""",
                ["ledger.read"]),
            new("Reclassify", "Moves a transaction.",
                """{"type":"object","properties":{"category":{"type":"string"}},"required":["category"]}""",
                ["ledger.write"]),
        ]);

        protected override Task<string> DispatchAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Calls.Add(name);
            return Task.FromResult("22.80");
        }
    }

    /// <summary>Answers with a tool call first, then with text.</summary>
    private sealed class ScriptedClient(string tool) : IChatClient
    {
        public int Turns { get; private set; }

        public IList<ChatMessage>? LastSent { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            LastSent = [.. messages];
            LastOptions = options;
            Turns++;

            if (Turns == 1)
            {
                var call = new FunctionCallContent(
                    callId: "1",
                    name: tool,
                    arguments: new Dictionary<string, object?> { ["category"] = "coffee" });

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Coffee came to 22.80.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static AgentCall Call() => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = "What did coffee cost?",
        Operation = "ILedger.AskAsync",
        Arguments = [],
    };

    [Fact]
    public async Task A_permitted_tool_is_called_and_its_result_reaches_the_answer()
    {
        var invoker = new Ledger();
        var client = new ScriptedClient("TotalFor");

        var answer = await new AgentRunner(client).CompleteWithToolsAsync(
            Call(), invoker, new GrantedPermissions(["ledger.read"]), ct: Ct);

        Assert.Equal(["TotalFor"], invoker.Calls);
        Assert.Equal("Coffee came to 22.80.", answer);
        Assert.Equal(2, client.Turns);
    }

    [Fact]
    public async Task The_model_is_only_offered_tools_this_caller_may_use()
    {
        var client = new ScriptedClient("TotalFor");

        await new AgentRunner(client).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), ct: Ct);

        // Reclassify is never mentioned, so the model cannot decide to want it.
        var offered = client.LastOptions!.Tools!.Select(t => t.Name);
        Assert.Equal(["TotalFor"], offered);
    }

    [Fact]
    public async Task A_denied_tool_is_refused_without_running_and_told_to_the_model()
    {
        var invoker = new Ledger();
        // The model asks for a tool it was not offered — a conversation can
        // outlive a permission, and this is the second enforcement point.
        var client = new ScriptedClient("Reclassify");

        var answer = await new AgentRunner(client).CompleteWithToolsAsync(
            Call(), invoker, new GrantedPermissions(["ledger.read"]), ct: Ct);

        Assert.Empty(invoker.Calls);
        // The turn completes rather than throwing: a denial is information the
        // model should have, and the person is waiting on an answer.
        Assert.Equal("Coffee came to 22.80.", answer);
        Assert.Equal(2, client.Turns);
    }

    [Fact]
    public async Task An_agent_with_no_permissions_is_offered_nothing()
    {
        var client = new ScriptedClient("TotalFor");

        await new AgentRunner(client).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions([]), ct: Ct);

        Assert.Empty(client.LastOptions!.Tools!);
    }

    [Fact]
    public async Task The_iteration_bound_is_refused_rather_than_silently_raised()
    {
        var runner = new AgentRunner(new ScriptedClient("TotalFor"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.CompleteWithToolsAsync(Call(), new Ledger(), GrantAllTools.Instance, 0, Ct));
    }
}
