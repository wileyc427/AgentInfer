# JSON contexts, and when you need one

Most projects never need one. Two situations do, and only one of them has
anything to do with trimming.

| | Needs a context |
| --- | --- |
| `Task<string>` reply | no — never JSON at all |
| `IAsyncEnumerable<string>` reply | no — never JSON either |
| `Task<Verdict>` or `Task<Severity>` reply | no — binds reflectively |
| A tool returning a scalar, an enum, or an array of those | no |
| A tool returning anything richer | **yes** — `AIN016`, a build error |
| Publishing with `PublishTrimmed` or `PublishAot` | **yes** — or binding breaks |

`samples/Ledger` is the fifth row, not the sixth. It declares a context
because `LedgerTools.Overview()` returns `IReadOnlyList<CategorySummary>`;
nothing in this repository is trimmed. Read it as the exception rather than
the rule.

## What a context is

Pre-generated serialization code. Not configuration, not a schema, not
validation.

`JsonSerializer.Serialize(rows)` compiles to *call this method, hand it this
object*. Nothing in the compiled output names the type's members — the
serializer finds them at run time with `GetProperties()`. A context makes
`System.Text.Json`'s own generator write them out instead:

```csharp
// obj/generated/…/LedgerJson.CategorySummary.g.cs
writer.WriteString(PropName_category, __value_Category);
writer.WriteNumber(PropName_spent,   ((CategorySummary)value).Spent);
writer.WriteNumber(PropName_budget,  ((CategorySummary)value).Budget);
```

That is the whole difference. One discovers the shape at run time; the other
had it written at build.

## Knowing the type is not enough

The type is right there in the signature, which makes it tempting to think the
compiler already has what it needs. It does not, and it says so:

```csharp
IReadOnlyList<CategorySummary> rows = [new("food", 42m, 50m)];

JsonSerializer.Serialize(rows);                        // IL2026, IL3050
JsonSerializer.Serialize(rows, LedgerJson.Default.…);  // clean
```

Same type, same value, same file. The compiler knows the type's **name**; it
never emits code touching its **members**. A trimmer follows compiled output,
sees nothing using them, and removes them.

## Reason one: trimming

Trimming deletes what your compiled code does not reference, to make the
published app smaller. It is off by default and you turn it on deliberately —
`PublishTrimmed` or `PublishAot`, usually for small containers, CLI tools or
serverless cold starts. A normal `dotnet run`, and a normal container image,
trim nothing.

When it is on, reflective binding loses whatever the trimmer removed, and the
failures are quiet:

- **Binding** returns defaults. A trimmed setter means `{"score": 4}` arrives
  as `Score = 0`.
- **Validation fails open.** `Validator.TryValidateObject` enumerates the
  properties, finds none left, and returns **true** — a pass, not an error, on
  exactly the value a model is most likely to get wrong.

Neither raises anything at run time. That is the argument for the typed path:
not that it is faster or stricter, but that it cannot quietly stop working.

Without a context the reflective path stays, annotated with
`[RequiresUnreferencedCode]` rather than hidden, so a trimmed publish reports
`IL2026` at every call site instead of failing in production.

## Reason two: a tool result no overload can render

A tool's result has to become text for the model. `ToolResult.Render` is a
closed set of overloads — the scalars, enums, and arrays of those — that write
JSON by hand with no reflection anywhere. A type outside that set matches no
overload, and the tool path has no reflective fallback on purpose, so it is
`AIN016` and the build stops.

This one applies on every machine, trimmed or not, which makes it the more
common reason to meet a context and the less obvious one.

The message names both ways out: declare a context, or return a simpler shape.
`string[]` needs nothing, at the cost of the structure the model receives.

## What you write

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Verdict))]
[JsonSerializable(typeof(IReadOnlyList<CategorySummary>))]
internal partial class LedgerJson : JsonSerializerContext;

[assembly: AgentInferJson(typeof(LedgerJson))]
```

Two generators read this, and neither can do the other's job:

| Line | Read by | Meaning |
| --- | --- | --- |
| `[JsonSerializable(typeof(X))]` | System.Text.Json's generator | write the read and write code for `X` |
| `[assembly: AgentInferJson(…)]` | AgentInfer's generator | bind through that class |

`[JsonSerializable]` alone gives you code AgentInfer never finds.
`[assembly: AgentInferJson]` alone names a context with nothing in it. The
assembly attribute is assembly-wide because a project may hold several
contexts — an ASP.NET app usually has its own — and the generator has to be
told which one is for agents rather than guess.

The options are not decoration. They have to match what the reflective path
binds with, or the same reply binds differently depending on which overload a
method happened to take; `AIN017` says so at build. See
[AIN017](authoring.md#ain017).

## One file, and one is enough

A context is a bag of `JsonTypeInfo` with no notion of what each entry is for.
The same one serves every agent and every tool in the assembly —
`samples/Ledger` registers a tool result and a reply type side by side — and
`[assembly: AgentInferJson]` is `AllowMultiple = false` because there is
nothing a second context could do that another line in the first does not.

It also has to be one *file*. Splitting the `partial` across two looks
reasonable and fails with an error that reads like your mistake; see
[Gotchas](gotchas.md).

So: one file, named for the project, beside the agents and tools it serves —
`LedgerJson.cs` next to `LedgerAgents.cs` and `LedgerTools.cs`. A directory for
it would hold one file forever.

What is worth writing down is **why it exists**, because that is the part
nobody can recover later:

```csharp
// Required: LedgerTools.Overview() returns IReadOnlyList<CategorySummary>, which
// no ToolResult.Render overload takes — AIN016. Verdict rides along because the
// context is already here, not because anything in this project is trimmed.
internal partial class LedgerJson : JsonSerializerContext;
```

Two facts, and which of them is load-bearing. Delete `Overview()` a year from
now and that comment is the difference between removing the context and leaving
it in place because nobody could tell whether it was still needed.

## Why it is boilerplate

Because **Roslyn has no way for one generator to ask another for output.**

AgentInfer's generator can see that `LedgerJson` exists — it is a class in the
same compilation — but it cannot see what `System.Text.Json`'s generator wrote
for it, and it cannot ask for `CategorySummary` to be added. Writing that code
itself would mean reimplementing naming policies, converters, nullability,
nested types, collections and polymorphism, permanently drifting from the real
implementation.

So it delegates, and you carry the message between the two. These six lines are
a platform limitation rather than a rule this library chose, which is worth
knowing before you go looking for the setting that turns them off.

## Where to go next

[Typed replies](typed-replies.md) for what the context buys once it exists —
`IReplyContract`, compiled validation, and anticipated failures.
[Tools](tools.md) for the schema and permission rules on the other half.
