using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.AI;

namespace AgentInfer;

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
        var json = Render(Name, arguments);

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

    /// <summary>
    /// Writes the model's arguments back out as JSON, without a serializer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JsonSerializer.Serialize</c> over a
    /// <c>Dictionary&lt;string, object?&gt;</c> has to discover each value's
    /// type at run time — reflection, on the one path this library claims is
    /// free of it. It was there for the life of the repo, because the analyzer
    /// that would have said so was never switched on.
    /// </para>
    /// <para>
    /// Writing it by hand is not a workaround. The values arrive as
    /// <see cref="JsonElement"/> from the loop that parsed the model's reply,
    /// so copying them through is the honest operation and
    /// <see cref="Utf8JsonWriter"/> does exactly that with nothing to reflect
    /// over. The scalars below are for a caller that assembled arguments itself
    /// rather than receiving them from a model.
    /// </para>
    /// <para>
    /// Anything else throws, by name. A tool argument this cannot render is a
    /// shape the generated dispatcher could not have bound either, and a
    /// mangled value that dispatches is worse than a call that stops.
    /// </para>
    /// </remarks>
    private static string Render(string tool, AIFunctionArguments arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);

                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case JsonElement element: element.WriteTo(writer); break;
                    case JsonNode node: node.WriteTo(writer); break;
                    case string text: writer.WriteStringValue(text); break;
                    case bool flag: writer.WriteBooleanValue(flag); break;
                    case int number: writer.WriteNumberValue(number); break;
                    case long number: writer.WriteNumberValue(number); break;
                    case double number: writer.WriteNumberValue(number); break;
                    case float number: writer.WriteNumberValue(number); break;
                    case decimal number: writer.WriteNumberValue(number); break;

                    default:
                        throw new NotSupportedException(
                            $"'{tool}' was given '{name}' as {value.GetType().Name}, which has no JSON form here. "
                            + "Tool arguments arrive as JsonElement from the model; anything else must be a scalar.");
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Serializer options.</summary>
internal static class AgentJson
{
    /// <summary>For the loosely typed hop into the dispatcher.</summary>
    /// <summary>
    /// What <c>JsonSerializerOptions.Web</c> is, spelled out.
    /// </summary>
    /// <remarks>
    /// The <c>Web</c> getter carries a reflection-based type resolver and is
    /// annotated because of it, so reading it taints every path that touches
    /// these options — including tool dispatch, which has nothing reflective
    /// left in it. Three properties written out cost nothing and keep the
    /// annotation where it belongs.
    /// </remarks>
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

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
    private static JsonSerializerOptions? _binding;

    /// <summary>
    /// A method, not a property, so an annotation can reach the construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The non-generic <c>JsonStringEnumConverter</c> builds a converter per
    /// enum at run time, which Native AOT cannot do. That is true, and it only
    /// matters on the reflective path — which already says so in its own
    /// signature. A static property initializer runs in a class constructor and
    /// cannot inherit an annotation from anywhere, so the warning had nowhere
    /// to go; a method can carry it.
    /// </para>
    /// <para>
    /// The race on <c>_binding</c> is benign: two threads may each build one,
    /// and the two are equivalent.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode("Reflection-based JSON. An IReplyContract is the trimmable path.")]
    [RequiresDynamicCode("Reflection-based JSON. An IReplyContract is the trimmable path.")]
    public static JsonSerializerOptions Binding() =>
        _binding ??= new JsonSerializerOptions(Default)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
}
