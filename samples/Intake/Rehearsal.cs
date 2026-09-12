using Microsoft.Extensions.AI;

namespace Intake;

/// <summary>
/// A scripted model, so the sample runs with nothing installed.
/// </summary>
/// <remarks>
/// One class, because the runtime takes an <see cref="IChatClient"/> and
/// nothing else. It answers the extract badly the first time — urgency 9 out of
/// a declared 1–5 — so the repair loop in <see cref="IntakeAgent"/> has
/// something real to repair. Pass <c>--live</c> for a model that is trying.
/// </remarks>
internal sealed class Rehearsal : IChatClient
{
    private int _extracts;
    private int _listed;

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
        var binding = user.Contains("<answer>", StringComparison.Ordinal);

        if (user.Contains("What is this ticket about?", StringComparison.Ordinal))
        {
            // Unquoted. Told to reply with JSON and handed a list of words, a
            // model answers with the word about as often as with the quoted one.
            return Assistant("billing");
        }

        if (user.Contains("summarize what the customer is asking for", StringComparison.Ordinal))
        {
            if (toolsRan)
            {
                return Assistant(
                    "Ravensmere Dental were billed twice for the March seat license and once for a "
                    + "seat removed in February. They want both refunded and the seat count corrected "
                    + "before the next cycle.");
            }

            // Two calls, and the first is the point: Waiting takes an optional
            // `plan`, and this sends no arguments at all. Before the generator
            // understood defaults that was a KeyNotFoundException out of the
            // dispatch switch, so the empty object here is the end-to-end check
            // that an omitted optional argument reaches the method.
            return _listed++ == 0
                ? Call("Waiting", "{}")
                : Call("Ticket", "{\"id\":\"T-1041\"}");
        }

        if (user.Contains("extract the customer, the category", StringComparison.Ordinal))
        {
            if (!binding)
            {
                return toolsRan ? Assistant("Ticket read.") : Call("Ticket", "{\"id\":\"T-1041\"}");
            }

            // First bind is out of range and binds cleanly as an integer, which
            // is why [Range] does double duty: in the schema, and after.
            return Assistant(Interlocked.Increment(ref _extracts) == 1
                ? """
                  {"customer":"Ravensmere Dental","category":"billing","urgency":9,
                   "asks":["Refund the duplicate March charge","Correct the seat count"]}
                  """
                : """
                  {"customer":"Ravensmere Dental","category":"billing","urgency":4,
                   "asks":["Refund the duplicate March charge",
                           "Refund the seat removed in February",
                           "Correct the seat count before the next cycle"]}
                  """);
        }

        return Assistant("ok");
    }

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Call(string tool, string arguments) =>
        new(ChatRole.Assistant,
        [
            new FunctionCallContent(
                $"call-{Guid.NewGuid():N}",
                tool,
                Arguments(arguments)),
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
