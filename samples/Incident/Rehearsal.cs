using Microsoft.Extensions.AI;

namespace Incident;

/// <summary>
/// A scripted model, so the sample runs with nothing installed.
/// </summary>
/// <remarks>
/// <para>
/// Not a testing convenience bolted on — it is the design claim, exercised. The
/// runtime takes an <see cref="IChatClient"/> and nothing else: no HTTP, no
/// provider SDK, no key. So a model that always says the same thing is one
/// class, and a workflow's <em>shape</em> — how many operations, how many
/// requests, which branch the routing took, whether the critic loop terminates
/// — is testable without paying for a single token.
/// </para>
/// <para>
/// It is not a substitute for a real run and the sample says so. Every
/// interesting failure this library has fixed was found by pointing it at
/// qwen3: tool calls written into the message body as text, an iteration bound
/// that produced a confident wrong answer, a score of 100 out of five. A
/// scripted model reproduces none of those, because it is not trying to be
/// helpful. Use <c>--live</c> for that.
/// </para>
/// </remarks>
internal sealed class Rehearsal : IChatClient
{
    private int _reviews;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var all = messages as IList<ChatMessage> ?? [.. messages];
        var user = all.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var toolsRan = all.Any(m => m.Contents.Any(c => c is FunctionResultContent));

        return Task.FromResult(new ChatResponse(Reply(user, toolsRan)));
    }

    private ChatMessage Reply(string user, bool toolsRan)
    {
        // The binding half of the two-phase typed-with-tools path: no tools
        // offered, an <answer> to convert, and a schema to match.
        var binding = user.Contains("<answer>", StringComparison.Ordinal);

        if (user.Contains("How should this alert be handled?", StringComparison.Ordinal))
        {
            // Unquoted on purpose. Told to reply with JSON and given a list of
            // words, a model answers with the word about as often as with the
            // quoted word — the quotes look like formatting.
            return Assistant("page");
        }

        if (user.Contains("Which team owns", StringComparison.Ordinal))
        {
            return Assistant("platform");
        }

        if (user.Contains("Investigate this service", StringComparison.Ordinal))
        {
            if (binding) return Assistant(FindingJson(user));

            return toolsRan
                ? Assistant("Telemetry retrieved.")
                : Call("Snapshot");
        }

        if (user.Contains("Draft a postmortem", StringComparison.Ordinal))
        {
            return Assistant(
                "Checkout failures began at 02:10 when the payments service exhausted its "
                + "connection pool, caused by a bad deploy. Search and recommendations were "
                + "unaffected.");
        }

        if (user.Contains("Is every claim in this draft supported", StringComparison.Ordinal))
        {
            // Rejects once, then approves. The loop has to be seen to terminate,
            // and a critic that approves immediately demonstrates nothing.
            return Assistant(Interlocked.Increment(ref _reviews) == 1
                ? """
                  {"approved":false,"score":2,
                   "problems":["'caused by a bad deploy' is not in the findings."]}
                  """
                : """
                  {"approved":true,"score":4,"problems":[]}
                  """);
        }

        if (user.Contains("Revise this draft", StringComparison.Ordinal))
        {
            return Assistant(
                "Checkout failures began at 02:10 when the payments service exhausted its "
                + "connection pool, with its upstream bank API timing out. The cause is not "
                + "yet established. Search and recommendations were unaffected.");
        }

        if (user.Contains("Take command of this alert", StringComparison.Ordinal))
        {
            if (toolsRan && _commandRounds >= 2)
            {
                return Assistant(
                    "checkout-api and payments are both unhealthy; payments has an exhausted "
                    + "connection pool and checkout-api is returning 5xx from it. Search and "
                    + "recommendations are clean.");
            }

            _commandRounds++;

            return _commandRounds == 1
                ? Call("Services")
                : Call(("Investigate", "{\"service\":\"checkout-api\"}"), ("Investigate", "{\"service\":\"payments\"}"));
        }

        return Assistant("ok");
    }

    private int _commandRounds;

    private static string FindingJson(string user)
    {
        // The service name is in the <service> block the runner wrote.
        var service = Between(user, "<service>", "</service>").Trim();
        var healthy = service is "search" or "recommendations";

        var evidence = healthy
            ? "Error rate and p99 latency are both nominal."
            : "Error rate above 40% with p99 latency in the seconds; upstream dependency failing.";

        return $$"""
                 {"service":"{{service}}","healthy":{{(healthy ? "true" : "false")}},
                  "confidence":{{(healthy ? 95 : 88)}},"evidence":"{{evidence}}"}
                 """;
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return string.Empty;

        from += start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? string.Empty : text[from..to];
    }

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Call(string tool) => Call((tool, "{}"));

    private static ChatMessage Call(params (string Tool, string Arguments)[] calls) =>
        new(ChatRole.Assistant,
        [
            .. calls.Select((c, i) => (AIContent)new FunctionCallContent(
                $"call-{Guid.NewGuid():N}",
                c.Tool,
                Arguments(c.Arguments))),
        ]);

    /// <summary>
    /// Parses tool-call arguments without a serializer.
    /// </summary>
    /// <remarks>
    /// <c>Deserialize&lt;Dictionary&lt;string, object?&gt;&gt;</c> discovers the
    /// value types at run time, which is the reflection the rest of this path
    /// no longer does. <c>JsonNode</c> reads the same JSON with nothing to
    /// reflect over, and the values arrive as the <c>JsonElement</c>s the
    /// dispatcher expects anyway.
    /// </remarks>
    private static Dictionary<string, object?> Arguments(string json)
    {
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

        return parsed.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value?.GetValue<System.Text.Json.JsonElement>());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The sample does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
