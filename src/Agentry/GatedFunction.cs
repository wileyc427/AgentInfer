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
    private readonly JsonElement _schema;

    public GatedFunction(ToolDescriptor descriptor, ToolInvoker invoker, IToolAuthorizer authorizer)
    {
        _descriptor = descriptor;
        _invoker = invoker;
        _authorizer = authorizer;

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
            return await _invoker.InvokeAsync(Name, json, _authorizer, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolDeniedException denied)
        {
            // Returned rather than thrown. A denial is information the model
            // should have — it stops asking and says what it could not do —
            // whereas an exception aborts a turn the person is waiting on. The
            // authorization decision is unchanged either way: nothing ran.
            return $"Denied: {denied.Message}";
        }
    }
}

/// <summary>Serializer options for the loosely typed hop into the dispatcher.</summary>
internal static class AgentJson
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerOptions.Web);
}
