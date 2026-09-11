using System.ComponentModel.DataAnnotations;
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
    private static ActivitySource Source => AgentMetrics.Source;

    /// <summary>
    /// The caller's client, wrapped so every request is counted.
    /// </summary>
    /// <remarks>
    /// Wrapped here rather than left to the host's pipeline. See
    /// <see cref="CountingChatClient"/>: a budget a consumer has to remember to
    /// register is a budget that reads correctly and does nothing.
    /// </remarks>
    private readonly IChatClient _client =
        new CountingChatClient(client ?? throw new ArgumentNullException(nameof(client)));
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
    public async Task<T> CompleteJsonReflectivelyAsync<T>(
        AgentCall call,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var response = await _client.GetResponseAsync(Build(call, json: true), FormatFor(call), ct)
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
            var value = JsonSerializer.Deserialize<T>(Quoted(Unfence(text)), options ?? AgentJson.Binding());
            if (value is null) throw new AgentException(call.Operation, "the model returned JSON null");

            Validate(call, value, text);
            return value;
        }
        catch (JsonException error)
        {
            throw BindFailure<T>(call, text, error);
        }
    }

    /// <summary>
    /// Runs a typed method through a contract. No reflection, no annotation.
    /// </summary>
    /// <remarks>
    /// The overload that makes the typed path trimmable. Everything derived
    /// from the return type — schema, binding metadata, value rule — arrives in
    /// one object the caller supplies, so nothing here has to discover a shape
    /// at run time. See <see cref="IReplyContract{T}"/> for why the three
    /// travel together rather than separately.
    /// </remarks>
    public async Task<T> CompleteJsonAsync<T>(
        AgentCall call,
        IReplyContract<T> contract,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        RefuseDoubleSchema(call);

        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var described = call with { ResponseSchema = contract.Schema };

        var response = await _client.GetResponseAsync(Build(described, json: true), FormatFor(described), ct)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;
        Log(described, started, text.Length);

        return Bind(described, text, contract);
    }

    /// <summary>
    /// Runs a typed method and hands back what came of it, without throwing.
    /// </summary>
    /// <remarks>
    /// For a caller that means to repair. A reply that will not bind or will
    /// not validate comes back as a failed
    /// <see cref="ReplyAttempt{T}"/>; a refused connection, a spent budget or a
    /// cancellation still throws, because those are not outcomes the model
    /// produced.
    /// </remarks>
    public async Task<ReplyAttempt<T>> TryCompleteJsonAsync<T>(
        AgentCall call,
        IReplyContract<T> contract,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        RefuseDoubleSchema(call);

        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var described = call with { ResponseSchema = contract.Schema };

        var response = await _client.GetResponseAsync(Build(described, json: true), FormatFor(described), ct)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;
        Log(described, started, text.Length);

        return TryBind(described, text, contract, out _);
    }

    /// <summary>Binds through a contract, or explains why it could not.</summary>
    private static T Bind<T>(AgentCall call, string text, IReplyContract<T> contract)
    {
        var attempt = TryBind(call, text, contract, out var cause);

        return attempt.Succeeded
            ? attempt.Value
            : throw new AgentException(call.Operation, attempt.Problem!, cause);
    }

    /// <summary>
    /// Binds a reply, or says why it could not — without throwing.
    /// </summary>
    /// <remarks>
    /// The single binding path. <see cref="Bind{T}"/> is this plus a throw, so
    /// the two cannot disagree about what counts as a usable reply or about the
    /// sentence describing an unusable one.
    /// </remarks>
    private static ReplyAttempt<T> TryBind<T>(
        AgentCall call,
        string text,
        IReplyContract<T> contract,
        out Exception? cause)
    {
        cause = null;

        T? value;

        try
        {
            value = JsonSerializer.Deserialize(Quoted(Unfence(text)), contract.TypeInfo);
        }
        catch (JsonException error)
        {
            cause = error;
            return ReplyAttempt<T>.Failed(BindFailureMessage<T>(text, error));
        }

        if (value is null) return ReplyAttempt<T>.Failed("the model returned JSON null");

        return contract.Validate(value) is { } problem
            ? ReplyAttempt<T>.Failed(
                $"the reply bound to {typeof(T).Name} but failed validation: {problem}. Reply was: {Trim(text)}")
            : ReplyAttempt<T>.Ok(value);
    }

    /// <summary>
    /// The message a failed bind deserves, shared by both binding paths.
    /// </summary>
    /// <remarks>
    /// The raw text goes in the message rather than the log, because the thing
    /// you need when this fires is what the model actually said. The
    /// <see cref="JsonException"/> already names the property that was missing
    /// or null — the single most useful sentence available — and dropping it
    /// left "could not bind", which sends you to the wrong place.
    /// </remarks>
    private static AgentException BindFailure<T>(AgentCall call, string text, JsonException error) =>
        new(call.Operation, BindFailureMessage<T>(text, error), error);

    private static string BindFailureMessage<T>(string text, JsonException error)
    {
        var hint = LooksLikeToolCalls(text)
            ? $" The model answered with tool-call JSON instead of a {typeof(T).Name}, "
              + "which usually means it was asked for tools and for JSON output at once. "
              + "Smaller models resolve that by writing the calls out as text."
            : string.Empty;

        return $"could not bind the reply to {typeof(T).Name}. {error.Message}{hint} Reply was: {Trim(text)}";
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
    public async Task<T> CompleteJsonWithToolsReflectivelyAsync<T>(
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

        // Two phases, and the second one is not optional.
        //
        // Asking for tools and for JSON-only output in the same request is a
        // contradiction, and models resolve it badly. A real qwen3 run answered
        // by writing its tool calls into the message body as JSON text —
        // {"name": "TotalFor", "arguments": {"category": "books"}} — which the
        // loop never saw as tool calls and the binder then failed on. The model
        // was not malfunctioning; it was told to reply with JSON and did.
        //
        // So: run the loop with no JSON instruction and let it use tools
        // normally, then bind in a second call with no tools and nothing to be
        // confused by. One extra round trip, and the failure mode goes away
        // rather than being tuned around.
        var text = await RunWithToolsAsync(call, invoker, authorizer, maxIterations, json: false, started, ct)
            .ConfigureAwait(false);

        // The arguments come along, and dropping them was a bug. The binding
        // call is a fresh two-message request: a method taking a service name
        // and returning a record with a Service field was asked to produce one
        // from the answer text alone, and a model that could not find the name
        // in there invented one that read fine. Keeping them costs tokens
        // proportional to the inputs, which are the small half — the tool
        // output already in `answer` is the large one.
        var binding = call with
        {
            Operation = call.Operation + " (bind)",
            Arguments = [.. call.Arguments, new KeyValuePair<string, string>("answer", text)],
        };

        var reply = await _client.GetResponseAsync(Build(binding, json: true), FormatFor(binding), ct)
            .ConfigureAwait(false);

        return Bind<T>(call, reply.Text ?? string.Empty, options);
    }

    /// <summary>
    /// A tool-using typed method, through a contract. Also unannotated.
    /// </summary>
    /// <remarks>
    /// Same two phases as the options-based overload — the loop runs with no
    /// JSON instruction, then a second call binds with no tools — because
    /// asking for tools and JSON-only output at once is a contradiction models
    /// resolve by writing their tool calls into the message body as text.
    /// </remarks>
    public async Task<T> CompleteJsonWithToolsAsync<T>(
        AgentCall call,
        IReplyContract<T> contract,
        ToolInvoker invoker,
        IToolAuthorizer authorizer,
        int maxIterations = 6,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        RefuseDoubleSchema(call);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);

        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var described = call with { ResponseSchema = contract.Schema };

        var text = await RunWithToolsAsync(described, invoker, authorizer, maxIterations, json: false, started, ct)
            .ConfigureAwait(false);

        var binding = described with
        {
            Operation = described.Operation + " (bind)",
            Arguments = [.. described.Arguments, new KeyValuePair<string, string>("answer", text)],
        };

        var reply = await _client.GetResponseAsync(Build(binding, json: true), FormatFor(binding), ct)
            .ConfigureAwait(false);

        return Bind(described, reply.Text ?? string.Empty, contract);
    }

    /// <summary>A tool-using typed method that reports failure rather than throwing.</summary>
    public async Task<ReplyAttempt<T>> TryCompleteJsonWithToolsAsync<T>(
        AgentCall call,
        IReplyContract<T> contract,
        ToolInvoker invoker,
        IToolAuthorizer authorizer,
        int maxIterations = 6,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        RefuseDoubleSchema(call);

        using var activity = Source.StartActivity(call.Operation);
        var started = Stopwatch.GetTimestamp();

        var described = call with { ResponseSchema = contract.Schema };

        var text = await RunWithToolsAsync(described, invoker, authorizer, maxIterations, json: false, started, ct)
            .ConfigureAwait(false);

        var binding = described with
        {
            Operation = described.Operation + " (bind)",
            Arguments = [.. described.Arguments, new KeyValuePair<string, string>("answer", text)],
        };

        var reply = await _client.GetResponseAsync(Build(binding, json: true), FormatFor(binding), ct)
            .ConfigureAwait(false);

        return TryBind(described, reply.Text ?? string.Empty, contract, out _);
    }

    /// <summary>
    /// A schema on the call and a contract is a contradiction, not a
    /// precedence question: one of the two has been edited and the other has
    /// not, and nothing here can tell which. Same reading AGT009 gives a prompt
    /// named twice.
    /// </summary>
    private static void RefuseDoubleSchema(AgentCall call)
    {
        if (call.ResponseSchema.Length == 0) return;

        throw new ArgumentException(
            $"{call.Operation}: the call sets ResponseSchema and a contract was supplied. "
            + "The contract owns the schema — leave ResponseSchema unset.",
            nameof(call));
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
        Log(call, started, text.Length, log.Invocations);
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

    /// <summary>
    /// Options that ask the provider to enforce the shape, where it can.
    /// </summary>
    /// <remarks>
    /// Belt and braces with the schema in the prompt, because the two fail in
    /// different places: a provider that ignores response_format still sees the
    /// prompt, and a model that ignores the prompt is still constrained by the
    /// provider. Neither alone was enough in practice.
    /// </remarks>
    private static ChatOptions? FormatFor(AgentCall call)
    {
        if (call.ResponseSchema.Length == 0) return null;

        try
        {
            using var document = JsonDocument.Parse(call.ResponseSchema);

            // Only an object root goes to the provider. OpenAI-compatible
            // structured output requires one and rejects {"type":"string"}
            // outright, so handing it an enum's schema turns a call that would
            // have worked into a 400 — the belt breaking the braces. The prompt
            // still carries the schema, and for a closed set of words that is
            // the half that was doing the work anyway.
            if (!document.RootElement.TryGetProperty("type", out var kind) ||
                kind.GetString() != "object")
            {
                return null;
            }

            return new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    document.RootElement.Clone(),
                    schemaName: "result"),
            };
        }
        catch (JsonException)
        {
            // A schema the generator emitted should always parse. If it somehow
            // does not, the prompt still carries it — degrade rather than fail
            // a call over the belt when the braces are on.
            return null;
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
            user.Append("\n\nReply with JSON only. No prose, no markdown fence.");

            // The shape, not just the format. Without this a model is told to
            // reply with JSON and left to guess which JSON — a real run answered
            // {"supported": true} to a Verdict(bool, int, string[]), which is a
            // reasonable invention given nothing to go on.
            if (call.ResponseSchema.Length > 0)
            {
                user.Append("\n\nIt must match this JSON Schema exactly:\n").Append(call.ResponseSchema);
            }
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

    /// <summary>
    /// Quotes a bare word, so a one-word answer is still JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same class of repair as <see cref="Unfence"/>, and it earns its
    /// place for the same reason: it is what models actually do. Asked for an
    /// enum, told the legal values, and told to reply with JSON only, a model
    /// answers <c>high</c> at least as often as <c>"high"</c> — the word is the
    /// answer and the quotes look like formatting. Rejecting that is technically
    /// correct and practically a retry loop over punctuation.
    /// </para>
    /// <para>
    /// Deliberately narrow. Only an unbroken run of letters, digits and
    /// underscores qualifies, so prose never accidentally becomes a JSON
    /// string, and anything already JSON-shaped — a brace, a bracket, a quote,
    /// a digit-led number, <c>true</c>/<c>false</c>/<c>null</c> — is left
    /// exactly as it arrived.
    /// </para>
    /// </remarks>
    private static string Quoted(string text)
    {
        if (text.Length == 0) return text;

        if (!char.IsLetter(text[0]) && text[0] != '_') return text;

        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character) && character != '_') return text;
        }

        return text is "true" or "false" or "null" ? text : $"\"{text}\"";
    }

    /// <summary>
    /// Checks DataAnnotations on a bound reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding proves the shape; this proves the values. A real run answered
    /// <c>score: 100</c> for a field meant to be 1–5 and bound cleanly, because
    /// 100 is a perfectly good integer. The schema now carries the bound and
    /// tells the model, and this is the half that does not depend on the model
    /// having listened.
    /// </para>
    /// <para>
    /// The failure names the property and the rule, and quotes the reply, for
    /// the same reason the bind failure does: the useful thing is what the model
    /// actually said.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode("DataAnnotations validation walks the type with reflection.")]
    private static void Validate<T>(AgentCall call, T value, string text)
    {
        var results = new List<ValidationResult>();

        // Boxed once, deliberately. A value type boxes afresh at each use, and
        // TryValidateObject compares the instance it is given against the one
        // inside the context by reference — so passing `value!` twice throws
        // "the instance provided must match the ObjectInstance", from inside
        // validation, for every struct and enum return. Nothing caught it while
        // enums could not bind at all.
        object instance = value!;

        if (Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            return;
        }

        var problems = string.Join("; ", results.Select(r => r.ErrorMessage));

        throw new AgentException(
            call.Operation,
            $"the reply bound to {typeof(T).Name} but failed validation: {problems}. Reply was: {Trim(text)}");
    }

    /// <summary>
    /// Whether a reply is the model narrating tool calls rather than answering.
    /// </summary>
    /// <remarks>
    /// A specific, recognizable failure deserves a specific message. Without
    /// this the error is "could not bind", which sends you looking at your
    /// record type instead of at the request that confused the model.
    /// </remarks>
    private static bool LooksLikeToolCalls(string text) =>
        text.Contains("\"name\"", StringComparison.Ordinal) &&
        text.Contains("\"arguments\"", StringComparison.Ordinal);

    private static string Trim(string text) =>
        text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 399), "…");

    /// <summary>
    /// One line per generation method call, and one tick on the enclosing scope.
    /// </summary>
    /// <remarks>
    /// Called once per public entry point — including the two-phase typed tool
    /// path, which is one operation and two requests. That difference is the
    /// point of counting both.
    /// </remarks>
    private void Log(AgentCall call, long started, int length, int toolCalls = 0)
    {
        AgentScope.RecordOperation(toolCalls);

        _logger.LogInformation(
            "{Operation} completed in {Elapsed:F1}s, {Length} chars",
            call.Operation,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            length);
    }
}
