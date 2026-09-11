using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Agentry;

/// <summary>
/// One tool, as <c>Microsoft.Extensions.AI</c> sees it, with the permission
/// check inside it.
/// </summary>
/// <remarks>
/// <para>
/// Deriving from <see cref="AIFunction"/> by hand rather than calling
/// <c>AIFunctionFactory.Create</c>. The factory reflects over a delegate to
/// build the schema and bind arguments, which is exactly the thing this library
/// moved to compile time — using it here would put reflection back on the path
/// and undo the trimming story for the sake of three lines.
/// </para>
/// <para>
/// Putting the check <em>in the function</em> rather than around the loop is
/// what makes it hold no matter who drives. Whether the loop is ours,
/// <c>FunctionInvokingChatClient</c>'s, or something a consumer wrote, the only
/// way to the tool is through <see cref="InvokeCoreAsync"/>.
/// </para>
/// </remarks>
internal sealed class GatedFunction : AIFunction
{
    private readonly ToolDescriptor _descriptor;
    private readonly ToolInvoker _invoker;
    private readonly IToolAuthorizer _authorizer;
    private readonly ToolCallLog _log;
    private readonly JsonElement _schema;

    public GatedFunction(
        ToolDescriptor descriptor,
        ToolInvoker invoker,
        IToolAuthorizer authorizer,
        ToolCallLog log)
    {
        _descriptor = descriptor;
        _invoker = invoker;
        _authorizer = authorizer;
        _log = log;

        // Parsed once. The string came from the generator and cannot change.
        _schema = JsonDocument.Parse(descriptor.ParametersSchema).RootElement.Clone();
    }

    public override string Name => _descriptor.Name;

    public override string Description => _descriptor.Description;

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Back to JSON so the generated dispatcher can bind with the accessors
        // it chose at compile time. A round trip, and worth it: the alternative
        // is a second binding path that reads the loosely typed dictionary and
        // has to re-derive types the generator already knew.
        var json = JsonSerializer.Serialize(
            arguments.ToDictionary(pair => pair.Key, pair => pair.Value),
            AgentJson.Default);

        try
        {
            var result = await _invoker.InvokeAsync(Name, json, _authorizer, cancellationToken)
                .ConfigureAwait(false);

            // Counted here rather than in the invoker: the invoker outlives the
            // call, and "calls per turn" is the distribution that matters.
            _log.Record(Name, denied: false);
            AgentMetrics.ToolCalls.Add(1, new KeyValuePair<string, object?>("tool", Name),
                new KeyValuePair<string, object?>("outcome", "ok"));

            return result;
        }
        catch (ToolDeniedException denied)
        {
            _log.Record(Name, denied: true);
            AgentMetrics.ToolCalls.Add(1, new KeyValuePair<string, object?>("tool", Name),
                new KeyValuePair<string, object?>("outcome", "denied"));

            // Returned rather than thrown. A denial is information the model
            // should have — it stops asking and says what it could not do —
            // whereas an exception aborts a turn the person is waiting on. The
            // authorization decision is unchanged either way: nothing ran.
            return $"Denied: {denied.Message}";
        }
    }
}

/// <summary>Serializer options.</summary>
internal static class AgentJson
{
    /// <summary>For the loosely typed hop into the dispatcher.</summary>
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerOptions.Web);

    /// <summary>
    /// For binding a reply to a return type — where the annotations are meant
    /// to be a contract rather than a suggestion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without these two flags the library's central claim is false at the one
    /// place it matters. A model replied <c>{"approved":false,"score":0}</c> to
    /// a method returning <c>Verdict(bool, int, string[] Problems)</c>, and
    /// deserialization happily produced a record with <c>null</c> in the
    /// non-nullable <c>Problems</c> slot. The caller's <c>foreach</c> then threw
    /// a NullReferenceException several lines away from the cause.
    /// </para>
    /// <para>
    /// <c>Task&lt;Verdict&gt;</c> has to mean a <c>Verdict</c>. With these on,
    /// the same reply raises a JsonException at the boundary, which the runner
    /// turns into an error naming the missing property and quoting what the
    /// model actually said.
    /// </para>
    /// <para>
    /// This covers shape, not semantics. The same reply scored 0 out of an
    /// intended 1–5 and nothing objected, because no range was declared. Value
    /// constraints would need DataAnnotations or IValidatableObject run after
    /// binding — worth doing, and a separate decision from making the type
    /// itself honest.
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Binding { get; } = new(JsonSerializerOptions.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
}
