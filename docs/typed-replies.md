# Typed replies

Three things have to agree about a typed reply: **the schema the model is told,
the metadata the reply is bound with, and the rule its values must satisfy.**
They used to live in three places — a string on `AgentCall`, a
`JsonSerializerOptions`, and reflection inside the runner — and nothing kept
them in step. This repo paid for that twice: `{"supported": true}` because the
schema was missing, then `score: 100` because the schema and the validator
disagreed about what an integer meant.

```csharp
public interface IReplyContract<T>
{
    string Schema { get; }             // what the model is told
    JsonTypeInfo<T> TypeInfo { get; }  // how the reply is bound
    string? Validate(T value);         // what the values must satisfy
}
```

Generated, they come from one read of the return type and its attributes in one
pass, so the `"minimum":1,"maximum":5` in the schema and the `is < 1 or > 5` in
the check **cannot** drift:

```csharp
file sealed class ReviewAsyncContract : IReplyContract<Verdict>
{
    public string Schema => @"…""score"":{""type"":""integer"",""minimum"":1,""maximum"":5}…";

    public JsonTypeInfo<Verdict> TypeInfo => Info;

    public string? Validate(Verdict value)
    {
        if (value.Score is < 1 or > 5) return $"score must be between 1 and 5, not {value.Score}";
        return null;
    }
}
```

It is also what makes the typed path trimmable. `JsonTypeInfo<T>` comes from
`System.Text.Json`'s own generator, so binding carries no
`[RequiresUnreferencedCode]`; and validation moves off DataAnnotations, which is
reflective, has no source-generated equivalent, and — worse — **fails open**
under trimming: with the property metadata gone it finds nothing to check and
reports success, on precisely the value a model is most likely to get wrong.

## You declare the context; the generator points at it

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Verdict))]
internal partial class LedgerJson : JsonSerializerContext;

[assembly: AgentInferJson(typeof(LedgerJson))]
```

Six lines, and they cannot be emitted for you. **Roslyn generators do not
chain**: a `JsonSerializerContext` written by this generator is invisible to
`System.Text.Json`'s and compiles to an abstract class with no metadata in it —
the error is *"does not implement inherited abstract member GetTypeInfo(Type)"*.
Their *outputs* can reference each other, because both land in the same
compilation. Only their inputs cannot.

Without the attribute the reflective path stays, annotated and honest.
`samples/Incident` deliberately declares no context, because opt-in is only
opt-in if something opts out.

Two diagnostics keep the halves lined up, and they exist because the generator
can read the attributes driving the *other* generator even though it cannot see
its output. `AIN013` catches `[AgentInferJson]` pointing at something that is not a
context. `AIN014` catches a return type the context does not serialize:

> `'IAnalyst.SummariseAsync' returns 'Summary', which 'LedgerJson' does not
> serialize. Add [JsonSerializable(typeof(Summary))] to it.`

## An anticipated failure is returned, not thrown

A model answering `urgency: 9` against a declared 1–5 has not malfunctioned. It
has done something the caller anticipates and handles by asking again with the
problem attached — and a repair loop built on `try`/`catch` made every ordinary
run of `samples/Intake` report two first-chance exceptions in a debugger. They
were harmless, and they read as a failure.

```csharp
var attempt = await runner.TryCompleteJsonAsync(call, ExtractContract.Instance, ct);
if (attempt.Succeeded) return attempt.Value;

var repair = call with { Arguments = [.. call.Arguments, new("previousAttemptFailed", attempt.Problem!)] };
return await runner.CompleteJsonAsync(repair, ExtractContract.Instance, ct);
```

`CompleteJsonAsync` still throws, because most callers do not repair and forcing
all of them through a result type to serve the few who do would be a tax. It is
implemented by calling the `Try` path, so there is **one** binding path rather
than two that can disagree about what counts as a usable reply — and
`attempt.Problem` is the same sentence the exception would have carried, because
that string is what gets handed back to the model.

Only a reply that will not bind or will not validate comes back as a failed
attempt. A refused connection, a spent budget or a cancellation still throws:
those are not outcomes the model produced.

## Rendering a tool result

The mirror image, and the last reflective call in the tool path. The generated
dispatch bound its arguments with accessors chosen at compile time and then
handed the result back through `JsonSerializer.Serialize<T>(result)`, which
picks a converter from the run-time type.

```csharp
// scalar, enum, or an array of those — the compiler picks the overload
return global::AgentInfer.ToolResult.Render(result);

// anything richer — a JsonTypeInfo from the declared context
return JsonSerializer.Serialize(result, (JsonTypeInfo<IReadOnlyList<CategorySummary>>)…);
```

The overload set is deliberately the same set `SchemaWriter` accepts as a tool
*parameter*. Arguments and results travel the same wire, and a result richer
than anything a parameter may be is a signal the tool is returning a document
rather than an answer.

With neither available it is `AIN016`, an error rather than a reflective
fallback — and the asymmetry with the reply path is deliberate. A reply type is
one per method and visible in the signature; tools are a menu that grows, and a
silent fallback is exactly how the parameter schemas would have rotted if they
had not been compile-time from the start.
