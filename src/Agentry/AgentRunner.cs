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
    private static readonly ActivitySource Source = new("Agentry");

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
        using var activity = Source.StartActivity(call.Operation);
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
        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var response = await _client.GetResponseAsync(Build(call, json: true), cancellationToken: ct)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;
        Log(call, started, text.Length);

        return Bind<T>(call, text, options);
    }

    /// <summary>Binds a reply, or explains why it could not.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON.")]
    [RequiresDynamicCode("Reflection-based JSON.")]
    private static T Bind<T>(AgentCall call, string text, JsonSerializerOptions? options)
    {
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

    /// <summary>
    /// Runs a method that may call tools, and returns its text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop is <c>FunctionInvokingChatClient</c>'s, not ours. It is the
    /// platform's, it is well tested, and it already handles parallel calls and
    /// per-call failures — writing a second one would be the same mistake as
    /// wrapping <see cref="IChatClient"/>.
    /// </para>
    /// <para>
    /// Authorization does not depend on that choice. The gate lives inside
    /// <see cref="GatedFunction"/>, so whichever loop drives, the only route to
    /// a tool is through the check. The menu handed over is already filtered by
    /// <see cref="ToolInvoker.AvailableTo"/>, so the model is never told about
    /// tools this caller may not use — and the function checks again anyway,
    /// because a conversation can outlive a permission.
    /// </para>
    /// </remarks>
    public async Task<string> CompleteWithToolsAsync(
        AgentCall call,
        ToolInvoker invoker,
        IToolAuthorizer authorizer,
        int maxIterations = 6,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);

        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        return await RunWithToolsAsync(call, invoker, authorizer, maxIterations, json: false, started, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a tool-using method whose result is bound from JSON.
    /// </summary>
    /// <remarks>
    /// Symmetrical with the text version on purpose. The alternative considered
    /// was "tool-using methods must return string", which is simpler to
    /// implement and worse to use: it makes whether a method gets tools depend
    /// on its return type, which is a rule nobody would guess and everybody
    /// would trip over.
    /// <para>
    /// The loop resolves the tool calls first; what is bound is the final
    /// assistant message, exactly as in the plain JSON path.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode("Binds the result with reflection-based JSON. A generated JsonSerializerContext replaces this.")]
    [RequiresDynamicCode("Binds the result with reflection-based JSON. A generated JsonSerializerContext replaces this.")]
    public async Task<T> CompleteJsonWithToolsAsync<T>(
        AgentCall call,
        ToolInvoker invoker,
        IToolAuthorizer authorizer,
        int maxIterations = 6,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);

        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var text = await RunWithToolsAsync(call, invoker, authorizer, maxIterations, json: true, started, ct)
            .ConfigureAwait(false);

        return Bind<T>(call, text, options);
    }

    private async Task<string> RunWithToolsAsync(
        AgentCall call,
        ToolInvoker invoker,
        IToolAuthorizer authorizer,
        int maxIterations,
        bool json,
        long started,
        CancellationToken ct)
    {
        var available = invoker.AvailableTo(authorizer);

        // One log per generation method call, because "calls per turn" is the
        // distribution that decides whether this workload ever needs the model
        // to write code. A counter shared across turns cannot produce it.
        var log = new ToolCallLog { Offered = available.Count, Total = invoker.Manifest.Tools.Count };

        var options = new ChatOptions
        {
            Tools = [.. available.Select(d => new GatedFunction(d, invoker, authorizer, log))],
        };

        using var looping = new FunctionInvokingChatClient(_client)
        {
            // The bound that stops a model and a tool trading turns until
            // something else does — usually a bill.
            MaximumIterationsPerRequest = maxIterations,
        };

        var response = await looping.GetResponseAsync(Build(call, json), options, ct).ConfigureAwait(false);

        var text = response.Text ?? string.Empty;
        // Activity.Current, not a parameter: the span was started by whichever
        // public method the caller entered through, and threading it down would
        // be plumbing to reach something already ambient.
        Record(call, log);
        Log(call, started, text.Length);
        return text;
    }

    /// <summary>
    /// Emits what the turn actually cost in tool calls.
    /// </summary>
    /// <remarks>
    /// The log line is for eyeballing during development; the histogram is for
    /// deciding. Its p95 across a real workload is the number that says whether
    /// composing tool calls in generated code would buy anything — which is a
    /// question worth answering with data rather than with taste, because the
    /// answer changes what gets built next.
    /// </remarks>
    private void Record(AgentCall call, ToolCallLog log)
    {
        var activity = Activity.Current;
        var operation = new KeyValuePair<string, object?>("operation", call.Operation);

        AgentMetrics.CallsPerTurn.Record(log.Invocations, operation);
        AgentMetrics.ToolsOffered.Record(log.Offered, operation);

        activity?.SetTag("agentry.tools.offered", log.Offered);
        activity?.SetTag("agentry.tool.calls", log.Invocations);
        activity?.SetTag("agentry.tool.denials", log.Denials);

        _logger.LogInformation(
            "{Operation}: {Offered} of {Total} tools offered, {Calls} call(s){Denied} — {Breakdown}",
            call.Operation,
            log.Offered,
            log.Total,
            log.Invocations,
            log.Denials > 0 ? $", {log.Denials} denied" : string.Empty,
            log);
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
