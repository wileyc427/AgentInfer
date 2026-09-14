using System.Text.Json;

using Microsoft.Extensions.AI;

using Xunit;

namespace AgentInfer.Tests;

/// <summary>
/// The shape of what goes to the model, pinned.
/// </summary>
/// <remarks>
/// <para>
/// Two things depend on this and neither is visible from
/// <see cref="AgentRunner"/>. A caller that manages its own context wraps the
/// runner's <see cref="IChatClient"/> and splices history into the message
/// list, which means it has to recognise the first request of a method call —
/// and the only thing distinguishing it from a tool-loop round is that it
/// carries exactly two messages. And the placement of the instruction relative
/// to the arguments is a prompting decision, not an implementation detail.
/// </para>
/// <para>
/// Both were true before these tests and neither was enforced, so a tidy-up
/// inside <c>Build</c> could have broken every wrapper in the wild silently.
/// </para>
/// </remarks>
public sealed class PromptLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records the message list of every request, in order.</summary>
    private sealed class Recorder(string reply, int toolCalls = 0) : IChatClient
    {
        private int _made;

        public List<IList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add([.. messages]);

            var message = _made++ < toolCalls
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{_made}", "TotalFor", new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, reply);

            return Task.FromResult(new ChatResponse(message));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add([.. messages]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class Ledger : ToolInvoker
    {
        public override ToolManifest Manifest { get; } = new(
            [new("TotalFor", "Reads.", """{"type":"object","properties":{}}""", ["ledger.read"])]);

        protected override Task<string> DispatchAsync(string name, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult("22.80");
    }

    private const string Instruction = "Answer the customer's latest message.";

    private static AgentCall Call(params KeyValuePair<string, string>[] arguments) => new()
    {
        SystemPrompt = "You are a support agent.",
        TaskPrompt = Instruction,
        Operation = "ISupport.ReplyAsync",
        Arguments = arguments,
    };

    private static string UserText(IList<ChatMessage> request) =>
        request.Last(m => m.Role == ChatRole.User).Text ?? string.Empty;

    /// <summary>
    /// Every entry point opens the same way, which is what a wrapper keys on.
    /// </summary>
    /// <remarks>
    /// Not a restatement of <c>Build</c> — the point is that no path decorates
    /// the opening request with anything extra. A context-managing decorator
    /// tells "start of a method call" from "another round of the tool loop" by
    /// this count, so a third message added here would make it splice history
    /// into the middle of a loop.
    /// </remarks>
    [Fact]
    public async Task The_first_request_of_every_call_is_one_system_and_one_user_message()
    {
        var text = new Recorder("ok");
        await new AgentRunner(text).CompleteTextAsync(Call(), Ct);

        var json = new Recorder("""{"value":3,"reason":"ok"}""");
        await new AgentRunner(json).CompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        var tools = new Recorder("ok");
        await new AgentRunner(tools).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), ct: Ct);

        var streaming = new Recorder("ok");
        await foreach (var _ in new AgentRunner(streaming).StreamTextAsync(Call(), Ct)) { }

        foreach (var recorder in new[] { text, json, tools, streaming })
        {
            var first = recorder.Requests[0];

            Assert.Equal(2, first.Count);
            Assert.Equal(ChatRole.System, first[0].Role);
            Assert.Equal("You are a support agent.", first[0].Text);
            Assert.Equal(ChatRole.User, first[1].Role);
        }
    }

    /// <summary>The discriminator, from the other side.</summary>
    [Fact]
    public async Task A_tool_loop_round_carries_more_than_two_messages()
    {
        var client = new Recorder("ok", toolCalls: 2);

        await new AgentRunner(client).CompleteWithToolsAsync(
            Call(), new Ledger(), new GrantedPermissions(["ledger.read"]), maxIterations: 10, ct: Ct);

        // Three requests: two rounds asking for the tool, then the answer. Only
        // the first is two messages; the rest carry the assistant's tool call
        // and the tool's result, which is what makes the count usable as a
        // "this is a continuation" signal.
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal([2, 4, 6], client.Requests.Select(r => r.Count));
    }

    [Fact]
    public async Task Arguments_are_tagged_by_name_and_rendered_in_declaration_order()
    {
        var client = new Recorder("ok");

        await new AgentRunner(client).CompleteTextAsync(
            Call(new KeyValuePair<string, string>("transcript", "a prior exchange"), new("message", "any update?")), Ct);

        // Declaration order is not cosmetic: it decides which prefix consecutive
        // turns share, and therefore whether a provider's prompt cache can hit.
        // Context first, varying input last.
        Assert.Equal(
            $"{Instruction}\n\n<transcript>\na prior exchange\n</transcript>\n\n<message>\nany update?\n</message>",
            UserText(client.Requests[0]));
    }

    [Fact]
    public async Task A_short_call_states_its_instruction_once()
    {
        var client = new Recorder("ok");

        await new AgentRunner(client).CompleteTextAsync(Call(new KeyValuePair<string, string>("message", "any update?")), Ct);

        // Nothing is buried in a message this size, and saying it twice either
        // side of four words reads as a formatting error rather than emphasis.
        var user = UserText(client.Requests[0]);
        Assert.Equal(Instruction.Length, user.LastIndexOf(Instruction, StringComparison.Ordinal) + Instruction.Length);
        Assert.EndsWith("</message>", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_argument_puts_the_instruction_back_at_the_end()
    {
        var client = new Recorder("ok");
        var transcript = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"customer: line {i}\nagent: reply {i}"));

        await new AgentRunner(client).CompleteTextAsync(
            Call(new KeyValuePair<string, string>("transcript", transcript), new("message", "any update?")), Ct);

        var user = UserText(client.Requests[0]);

        // Without this the last thing the model reads before generating is a
        // closing tag, with the instruction two thousand characters back. The
        // layout is fixed inside Build, so a caller passing a long argument has
        // no way to correct it from the interface.
        Assert.StartsWith(Instruction, user, StringComparison.Ordinal);
        Assert.EndsWith(Instruction, user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_restated_instruction_comes_before_the_format_rules_not_after()
    {
        var client = new Recorder("""{"value":3,"reason":"ok"}""");
        var transcript = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"customer: line {i}\nagent: reply {i}"));

        await new AgentRunner(client).CompleteJsonAsync(
            Call(new KeyValuePair<string, string>("transcript", transcript)), ScoreContract.Instance, Ct);

        var user = UserText(client.Requests[0]);

        // What to do, then how to format it. Restating the task after the schema
        // would bury the schema instead, which trades one recency problem for
        // another.
        Assert.True(
            user.LastIndexOf(Instruction, StringComparison.Ordinal)
            < user.IndexOf("Reply with JSON only", StringComparison.Ordinal));

        Assert.EndsWith(ScoreContract.Instance.Schema, user, StringComparison.Ordinal);
    }
}
