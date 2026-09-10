using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agentry;

/// <summary>
/// What generated agents call. One model round trip, bound to a return type.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small, and deliberately not a "chat abstraction".
/// <see cref="IChatClient"/> already is one, and it is the platform's. Wrapping
/// it would make this library a rival to <c>Microsoft.Extensions.AI</c> instead
/// of a layer on top of it, and would put a second provider stack on the
/// maintenance bill for no gain.
/// </para>
/// <para>
/// There is no loop here. A Predict method is one request and one response; the
/// loop belongs to CodeAct, which is P3, and hiding a latent loop in the Predict
/// path would make the cheap strategy quietly expensive.
/// </para>
/// </remarks>
public sealed class AgentRunner(IChatClient client, ILogger<AgentRunner>? logger = null)
{
    private static readonly ActivitySource Activity = new("Agentry");

    private readonly IChatClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ILogger _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Runs a method whose return type is <see cref="string"/>.</summary>
    /// <remarks>
    /// Separate from the JSON path because a string needs no schema, no parsing
    /// and no repair — asking a model to wrap prose in JSON so it can be
    /// unwrapped again is a round trip's worth of ways to fail for nothing.
    /// </remarks>
    public async Task<string> CompleteTextAsync(AgentCall call, CancellationToken ct = default)
    {
        using var activity = Activity.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var response = await _client.GetResponseAsync(Build(call, json: false), cancellationToken: ct)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;
        Log(call, started, text.Length);
        return text;
    }

    /// <summary>Runs a method whose return type is bound from JSON.</summary>
    /// <remarks>
    /// The reflective binding here is the one part of the library that is not
    /// AOT-clean; see <c>NotYetAotSafeAttribute</c>. The generator already knows
    /// every return type at build time, so the fix is a generated
    /// <c>JsonSerializerContext</c> — real work rather than a rename, which is
    /// why P1 ships the honest version instead of pretending.
    /// </remarks>
    [RequiresUnreferencedCode("Binds the result with reflection-based JSON. A generated JsonSerializerContext replaces this.")]
    [RequiresDynamicCode("Binds the result with reflection-based JSON. A generated JsonSerializerContext replaces this.")]
    public async Task<T> CompleteJsonAsync<T>(
        AgentCall call,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        using var activity = Activity.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var response = await _client.GetResponseAsync(Build(call, json: true), cancellationToken: ct)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;
        Log(call, started, text.Length);

        try
        {
            var value = JsonSerializer.Deserialize<T>(Unfence(text), options ?? JsonSerializerOptions.Web);
            return value ?? throw new AgentException(call.Operation, "the model returned JSON null");
        }
        catch (JsonException error)
        {
            // The raw text goes in the message rather than the log, because the
            // thing you need when this fires is what the model actually said.
            throw new AgentException(
                call.Operation,
                $"could not bind the reply to {typeof(T).Name}. Reply was: {Trim(text)}",
                error);
        }
    }

    private static ChatMessage[] Build(AgentCall call, bool json)
    {
        var user = new StringBuilder(call.TaskPrompt);

        foreach (var (name, value) in call.Arguments)
        {
            user.Append("\n\n<").Append(name).Append(">\n").Append(value).Append("\n</").Append(name).Append('>');
        }

        if (json)
        {
            // Belt and braces alongside whatever structured-output support the
            // provider has: local models in particular will happily wrap JSON in
            // a markdown fence regardless of what they were asked for, which is
            // what Unfence exists for.
            user.Append("\n\nReply with JSON only. No prose, no markdown fence.");
        }

        return
        [
            new ChatMessage(ChatRole.System, call.SystemPrompt),
            new ChatMessage(ChatRole.User, user.ToString()),
        ];
    }

    /// <summary>Strips a ```json fence, which small models add unbidden.</summary>
    private static string Unfence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        var body = trimmed[(firstNewline + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence < 0 ? body : body[..fence]).Trim();
    }

    private static string Trim(string text) =>
        text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 399), "…");

    private void Log(AgentCall call, long started, int length) =>
        _logger.LogInformation(
            "{Operation} completed in {Elapsed:F1}s, {Length} chars",
            call.Operation,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            length);
}
