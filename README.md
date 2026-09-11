# Agentry

Object-oriented agents for .NET. An agent is an interface, its prompt is an
attribute, and a Roslyn generator writes the implementation at build time.

```csharp
[Agent("""
    You answer questions about a household ledger.
    Use only figures you were given. Never invent an amount.
    """)]
public interface ILedgerAnalyst
{
    [Prompt("Summarise this spending against its budget in two sentences.")]
    public Task<string> SummariseAsync(string spending, CancellationToken ct = default);

    [Prompt("Judge whether this summary is supported by the figures given.")]
    public Task<Verdict> ReviewAsync(string summary, string figures, CancellationToken ct = default);
}
```

That is the whole authoring surface. `LedgerAnalystAgent` is generated,
implements the interface, and takes an `AgentRunner`. Consumers inject
`ILedgerAnalyst`, so mocking an agent in a test needs no framework support and
no model.

Design reasoning lives in
[`CSHARP-AGENTS-PROPOSAL.md`](https://github.com/wileyc427/desktop-toolkit/blob/main/docs/architecture/CSHARP-AGENTS-PROPOSAL.md).

## Where it is

**P1 and P2 done.** The generator, the attributes, a Predict runtime over
`Microsoft.Extensions.AI`, `[AgentTool]` with compile-time schemas, enforced
`[RequiresPermission]`, and a tool-calling loop — all verified end to end
against a stub endpoint. No sandbox and no CodeAct: that is P3 and it is not
started.

| Package | What it is |
| --- | --- |
| `Agentry.Abstractions` | The attributes. netstandard2.0, zero dependencies |
| `Agentry.Generator` | The Roslyn incremental generator |
| `Agentry` | The runtime generated code calls into |

## The two decisions worth knowing

**Predict is the absence of an attribute.** `[Strategy(Strategies.CodeAct)]` is
a line somebody typed, that a reviewer sees, and that `grep` finds. The Python
framework this follows defaults to executing model-written code, which meant
three methods in a project built against it were running generated Python
nobody had reviewed. Opting into code execution has to be visible.

**Prompts are string constants, not doc comments.** C# strips XML docs into a
separate file that is unreachable at run time. That reads like a handicap and
is the opposite: a constant survives compilation and trimming, and cannot fall
through to a base class's prompt the way a Python docstring silently can.

## The diagnostics are the product

| Rule | Replaces |
| --- | --- |
| `AGT001` Agent requires a prompt | an f-string is not a docstring, so you silently inherit the framework's internal prompt |
| `AGT002` Method requires `[Prompt]` | an empty task prompt; the method behaves almost right |
| `AGT003` Must return `Task<T>` | a return annotation the strategy cannot satisfy, discovered after paying for a call |
| `AGT004` CodeAct is not implemented | an undecorated method silently executing generated code |
| `AGT005` Unsupported tool parameter | a model sending a shape the parameter cannot take, learned from a trace |
| `AGT006` Tool requires `[RequiresPermission]` | `@hidden`, which keeps a method out of the docs and leaves it callable |

Each row is a real failure from building against NOOA, moved from production to
the build. The corollary is a rule this repo tries to hold: **a feature that
cannot be diagnosed at compile time should be questioned before it is added.**

## Tools

```csharp
public sealed class LedgerTools
{
    [AgentTool("The total spent in one category.")]
    [RequiresPermission("ledger.read")]
    public decimal TotalFor(string category) => ...;

    public string DebugDump() => ...;   // no attribute, so invisible and unreachable
}

[Agent("...", Tools = typeof(LedgerTools))]
public interface ILedgerAnalyst { ... }
```

The generator emits a `ToolManifest` as a **static property built at compile
time**, with a JSON Schema string per tool:

```csharp
public static ToolManifest Tools { get; } = new(new ToolDescriptor[]
{
    new(@"TotalFor",
        @"The total spent in one category.",
        @"{""type"":""object"",""properties"":{""category"":{""type"":""string""}},""required"":[""category""],""additionalProperties"":false}",
        new string[] { @"ledger.read" }),
});
```

That schema is the differentiating piece. The usual way to get one is
reflecting over the method at startup — which is what `AIFunctionFactory.Create`
does, and it works until somebody publishes trimmed and the parameter metadata
is gone. Here there is nothing to reflect over and nothing to trim, and it is
visible in review.

Tools are opt-in one method at a time. A public method without `[AgentTool]` is
**absent** from the manifest, not hidden from documentation while remaining
callable — which is what NOOA's `@hidden` actually does.

### Permissions are enforced, not declared

```csharp
var invoker = new LedgerAnalystAgentTools(new LedgerTools());
var caller  = new GrantedPermissions(["ledger.read"]);

await runner.CompleteWithToolsAsync(call, invoker, caller);
```

`AvailableTo` filters the menu the model is sent, so a tool this caller may not
use is one it is **never told about**. `InvokeAsync` checks again before
dispatch, because a conversation that began before a permission changed still
has the old tool written down in its context.

Two places is not redundancy — it is the same shape a permission gate has to
have anywhere a conversation can outlive a grant.

The check lives inside the `AIFunction`, not around the loop, so it holds
whichever loop drives. A denied call is **returned to the model** rather than
thrown: the model stops asking and says what it could not do, and the person
waiting on an answer still gets one. Nothing ran either way.

> **A change from the design note.** It proposed one narrowed facade *type* per
> permission set. That is combinatorial — the distinct sets a principal can hold
> is the powerset of the permissions in play, so eight permissions is 256
> generated types. Filtering the menu and gating dispatch gets the same property
> without the explosion.

The loop itself is `FunctionInvokingChatClient`'s, not ours. It is the
platform's and it already handles parallel calls and per-call failures — writing
a second one would be the same mistake as wrapping `IChatClient`.

### An agent with tools uses them

```csharp
var caller  = new GrantedPermissions(["ledger.read"]);
var invoker = new LedgerAnalystAgentTools(new LedgerTools());

ILedgerAnalyst analyst = new LedgerAnalystAgent(new AgentRunner(client), invoker, caller);

await analyst.SummariseAsync();   // calls tools, then answers
```

The generated constructor takes the invoker and the authorizer, and **both are
required**. An authorizer defaulting to "allow" would make the safe path the one
you have to remember, which is the wrong way round for a permission gate.

Every generation method gets tools, not only the ones returning `string`. Tying
tools to the return type would be a rule nobody would guess. For a typed return
the loop resolves the tool calls first and the final message is bound, exactly
as on the plain path.

`[Strategy(Strategies.Predict, MaxIterations = 8)]` bounds the loop. It is not
only a CodeAct setting — a method that can call tools can trade turns with them,
and that needs a bound wherever the turns come from.

Running the sample against a tool-calling stub:

```
tools: Categories, TotalFor, BudgetFor (of 4; the rest need permissions this caller lacks)

summary: Coffee is over budget by 7.80; everything else is within budget.
verdict: approved=True score=4/5
```

`Reclassify` requires `ledger.write`, so it is absent from what the model was
told — not refused, absent.

## Measuring whether you need generated code

Every generation method logs what the turn actually cost:

```
ILedger.SummariseAsync: 1 of 2 tools offered, 4 call(s) — TotalFor×4
```

and records three instruments under the `Agentry` meter, so the same numbers
reach whatever OpenTelemetry pipeline the host already runs:

| Instrument | |
| --- | --- |
| `agentry.tool.calls_per_turn` | histogram — **the number that decides** |
| `agentry.tools.offered` | histogram — how much permissions narrowed the menu |
| `agentry.tool.calls` | counter, tagged by tool and outcome |

Letting a model compose tool calls in code it writes buys exactly one thing —
fewer round trips — at the cost of executing that code. Obviously worth it at
fifteen calls a turn; obviously not at two. **Count first, decide after.** The
p95 of `calls_per_turn` on a real workload is the whole argument.

### What a real model changed

Two findings from pointing it at `qwen3` rather than at a stub. Neither showed
up in 50 passing tests.

**Tools and JSON output cannot be asked for in the same request.** The JSON path
appended *"Reply with JSON only. No prose, no markdown fence."* while also
offering tools. qwen3 resolved the contradiction by writing its tool calls into
the message body as text:

```
{"name": "Categories", "arguments": {}}
{"name": "TotalFor", "arguments": {"category": "books"}}
```

The loop never saw those as tool calls, and the binder then failed on them. The
model was not malfunctioning — it was told to reply with JSON and did.

A typed method with tools now runs in **two phases**: the loop with no JSON
instruction, then a binding call with no tools. One extra round trip, and the
failure mode stops existing rather than being tuned around. When binding does
fail on text that looks like tool calls, the error says so.

**A wrong iteration bound produces a confident wrong answer, not an error.**
`MaxIterations` was 8; answering through the per-category tools needs nine calls.
The model was cut off and wrote a summary from what it had — *"other categories
lack sufficient data"* — which is a plausible sentence and a false one.

That is the strongest argument for the instrumentation. The log says
`9 call(s) — TotalFor×4, BudgetFor×4, Categories×1`, and nine against a bound of
eight is immediately legible. Without it the only symptom is prose that reads
fine.

**A typed return was not actually a contract.** A model replied
`{"approved":false,"score":0}` to a method returning
`Verdict(bool Approved, int Score, string[] Problems)`. Deserialization produced
a record with **null** in the non-nullable `Problems` slot, and the caller's
`foreach` threw a `NullReferenceException` several lines from the cause.

`Task<Verdict>` has to mean a `Verdict`, so binding now sets
`RespectNullableAnnotations` and `RespectRequiredConstructorParameters`. The
same reply fails at the boundary with a message naming the missing property and
quoting what the model said.

This covers **shape, not semantics**: that reply also scored 0 out of an
intended 1–5 and nothing objected, because no range was declared. Value
constraints need DataAnnotations run after binding — worth doing, and a separate
decision from making the type itself honest.

### The cheaper fix, before reaching for generated code

`Categories` / `TotalFor` / `BudgetFor` is a chatty API: 1 + 2N calls to answer
one question. `Overview()` returns every category with its total and budget in
**one**.

Most "the model needs to loop over tools" problems are really "this tool API was
designed for a UI, where a caller knows which single row it wants." A model
asking an open question wants the whole table. Design tools for a caller
reasoning about all of it at once and the round trips that motivated generated
code stop existing.

Measured on `qwen3:latest`, same question, same agent:

| Tools offered | Calls | Result |
| --- | --- | --- |
| per-category only | 9 | cut off at the bound; confidently wrong |
| with `Overview` | **1** | "Coffee is over budget by $7.80." — correct |

One call, right answer. That is the case for generated code evaporating on
contact with a better tool API, and it is why the counting came before the
decision.

### One thing the counting revealed

Within a single call, **filtering the menu is what enforces a permission**. A
tool the caller may not use is not in `ChatOptions.Tools`, so there is no
`AIFunction` by that name for the loop to invoke — it never reaches the check
inside `GatedFunction`, and the log records zero calls rather than a denial.

The check still earns its place, just not there: it fires when an invoker
outlives a permission change between turns, or when something calls
`InvokeAsync` directly. Worth knowing which of the two gates is load-bearing
where.

## Build

Needs the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project samples/Ledger
```

The sample sets `EmitCompilerGeneratedFiles`, so what the generator produced is
readable at
`samples/Ledger/obj/generated/Agentry.Generator/Agentry.Generator.AgentGenerator/`.
Reading it is the fastest way to understand the library, and it is how the
string-routing bug in the first draft was found.

## Three gotchas that cost time here

**Analyzers do not flow transitively through `ProjectReference`.** `Agentry`
references the generator as an `Analyzer`, but a project referencing `Agentry`
gets the runtime and no generator. It works through a NuGet package; inside
this repo every consuming project references the generator again explicitly.
See `samples/Ledger/Ledger.csproj`.

**The generator must target netstandard2.0**, because it is loaded into the
compiler. Anything else produces an analyzer the SDK declines to load, with no
error anywhere — it presents as "my generator produced nothing".

**Never compare rendered type names.** `SymbolDisplayFormat.FullyQualifiedFormat`
keeps the `string` keyword alias rather than expanding it to `System.String`,
so a display-string comparison silently fails. Use `SpecialType`. The first
draft did it the wrong way, compiled cleanly, and JSON-encoded every string
argument into its own prompt.

## Licence

MIT.
