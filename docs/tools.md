# Tools

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

The same was not true of the **result** for far longer than it should have
been. Arguments came in through compile-time schemas and compile-time accessors,
and then the return value went back out through
`JsonSerializer.Serialize<T>(result)` — one line below, in the same generated
`switch`. It survived because nothing checked. See
[Rendering a tool result](typed-replies.md#rendering-a-tool-result).

Tools are opt-in one method at a time. A public method without `[AgentTool]` is
**absent** from the manifest, not hidden from documentation while remaining
callable — which is what NOOA's `@hidden` actually does.

## An argument the model may leave out

```csharp
[AgentTool("Search the bags. Give an item level to see only what is above it.")]
[RequiresPermission("bags.read")]
public string Search(string character, string? query = null, int? minItemLevel = null) => …;
```

`character` is in the schema's `required` list and the other two are not, so a
model may call this with `{"character":"Fillup"}` and the dispatcher fills in
the rest from the signature.

**A default is the only thing that makes an argument optional.** Not
nullability: `int? page` with no default is a required parameter in C#, and a
schema that disagreed with the signature beside it would be the worse of the two
to trust. The default is also rendered into the dispatch switch as the C#
literal that reproduces it, so what the signature promises and what an omitted
argument does are the same thing by construction.

What this replaces is a sentinel the model had to be told about in prose — pass
an empty string for no filter, pass 0 for no floor — and prose is the weakest
place to put a rule. It was worse than that: every parameter was `required` and
every one was read with `GetProperty`, so a model that sent nothing anyway got a
`KeyNotFoundException` out of the dispatch switch rather than an answer.

An explicit `"query": null` takes the default too. A model told an argument is
optional sends that about as readily as it omits the key, and the two plainly
mean the same thing.

## Permissions are enforced, not declared

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

## An agent with tools uses them

```csharp
var caller  = new GrantedPermissions(["ledger.read"]);
var invoker = new LedgerAnalystAgentTools(new LedgerTools());

ILedgerAnalyst analyst = new LedgerAnalystAgent(new AgentRunner(client), invoker, caller);

await analyst.SummarizeAsync();   // calls tools, then answers
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
