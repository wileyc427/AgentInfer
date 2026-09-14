# Bounding, counting, and deciding

```csharp
using var scope = AgentScope.Begin("incident.triage", maxRequests: 20);

var severity = await triage.SeverityAsync(alert);
var findings = await Task.WhenAll(services.Select(s => investigator.InvestigateAsync(s)));

logger.LogInformation("{Scope}", scope);
// incident.triage: 6 operation(s), 14 request(s), 4 tool call(s), 18432 in / 2106 out token(s) in 0.2s
```

`MaxIterations` bounds one method's tool loop. Nothing bounded the composition,
so six methods at sixteen iterations was ninety-six requests with no ceiling
anywhere — and *a bound rather than a suggestion* applies harder one level up
than it does down there.

**Operations and requests are different numbers and both are wanted.** A typed
tool-using method is one operation and at least three requests: one round to
call the tool, one to answer, one to bind. Bounding operations would have missed
the expensive half. The bound in the sample is 20 because the arithmetic says 14
— two classifications, then four services at three each — and the first number
written there was 12, which stopped the workflow on its first run rather than
returning a triage decision made from half a sweep.

It throws rather than truncating, which is the decision `MaxIterations` got
wrong the first time: cutting a model off at its bound and keeping what it
produced gave prose that read fine and said figures were unavailable when they
were right there.

| Instrument | |
| --- | --- |
| `agentinfer.workflow.operations` | histogram — generation method calls per scope |
| `agentinfer.workflow.requests` | histogram — requests actually sent |
| `agentinfer.workflow.duration` | histogram — seconds |
| `agentinfer.workflow.tokens.input` | histogram — prompt tokens per scope |
| `agentinfer.workflow.tokens.output` | histogram — generated tokens per scope |

Scopes nest and counts roll up, so an orchestration cannot look cheap by hiding
its work one level down. Per-call spans nest under the scope's span, under the
same `AgentInfer` source.

Two decisions in there worth stating, because both went the less obvious way.

**The counter is a `DelegatingChatClient` the runner puts around its own
client**, not something a host registers. An `AddAgentInferBudget()` a consumer
wires into their `IChatClient` pipeline is more idiomatic and has one fatal
property: forget it, and `Begin("x", maxRequests: 20)` compiles, reads
correctly, and does nothing. It also has to be a decorator rather than a check
in the runner, for the same reason the permission gate lives inside
`GatedFunction` — the tool loop's rounds belong to `FunctionInvokingChatClient`
and are invisible from above.

**The scope is ambient, and that is a concession.** An `AsyncLocal` is invisible
state in a library that otherwise insists on saying things out loud. What makes
it acceptable is that the declaration is not ambient: the bound is a number in a
`using` a reviewer reads before the work it governs, and only the plumbing
flows. Opened without a bound, a scope changes nothing at all.

> Writing the decorator turned up a bug nothing was catching.
> `DelegatingChatClient` disposes what it wraps, and the tool path builds a
> `FunctionInvokingChatClient` in a `using` per call — so **every tool-using
> call was closing the caller's `IChatClient`**. The ledger sample never saw it
> because its two calls use different clients, and a fake with a no-op
> `Dispose` cannot tell. On a host where `AddAgentInferModels` shares one client
> per role, the second call through that role fails. Ownership now stops at the
> decorator: a runner is handed a client, it does not create one, and it must
> not close one.

## Requests are not what you are billed for

A bound in requests treats them as interchangeable, and they are not. The first
thing anyone builds with tools is a step whose prompt grows by a tool result
every round, so the fourth request in a loop can cost several times the first.
`scope.Requests` says 14 either way.

Every request's usage is therefore collected as well, at the same place and for
the same reason — inside the tool loop, where each round's own numbers are
visible. Reading the loop's *final* response instead would double count, because
that response reports the rounds' sum.

```csharp
using var scope = AgentScope.Begin("incident.triage");
...
scope.Tokens          // TokenCounts(Input, Output, Total, Reasoning, CachedInput)
scope.UsageReported   // whether any provider actually said
```

and per method call, tagged by operation:

| Instrument | |
| --- | --- |
| `agentinfer.tokens.input` | histogram — prompt tokens per generation method call |
| `agentinfer.tokens.output` | histogram — generated tokens |
| `agentinfer.tokens.reasoning` | histogram — output tokens spent thinking |
| `agentinfer.tokens.cached_input` | histogram — input tokens served from a provider cache |

Tagged **by operation**, because the actionable form of "this got expensive" is
which method. A single process-wide number says the bill went up and leaves the
reader to guess where.

**`reasoning` is separate because it is the one that explains a latency nobody
can account for.** A `qwen3:latest` run of the ledger sample took 41 seconds to
return 105 characters. Nothing in a duration histogram distinguishes that from a
slow network; the reasoning count says the model was writing the whole time,
somewhere the reply does not show. Folded into `output`, that is invisible.

**`cached_input` is separate because every provider that offers it bills it at a
fraction.** It is already included in `input`, so a cost estimate that does not
subtract it is wrong in the expensive direction.

**Nothing reported is not the same as nothing spent.** Plenty of providers say
nothing — a local Ollama streaming without `include_usage`, for one. Those calls
record no measurement and log no token figure, rather than contributing a zero:
a histogram that takes a zero for every unmeasured call has a p50 of zero and
reads as a cheap workload. `UsageReported` is how you tell the two apart, and
`ToString()` leaves the token half off entirely rather than printing `0 in / 0
out`.

The accumulator that collects this is ambient, like the scope — the requests it
sums are sent by `FunctionInvokingChatClient`, which knows nothing about the
method that built it. But it is passed into the logging call as a **required
parameter** rather than read from ambient state there, so a code path that
forgot to open one does not compile. Read ambiently, it would have billed that
path's tokens to whatever accumulator happened to be above it, silently.

# Measuring whether you need generated code

Every generation method logs what the turn actually cost:

```
ILedger.SummarizeAsync: 1 of 2 tools offered, 4 call(s) — TotalFor×4
```

and records three instruments under the `AgentInfer` meter, so the same numbers
reach whatever OpenTelemetry pipeline the host already runs:

| Instrument | |
| --- | --- |
| `agentinfer.tool.calls_per_turn` | histogram — **the number that decides** |
| `agentinfer.tools.offered` | histogram — how much permissions narrowed the menu |
| `agentinfer.tool.calls` | counter, tagged by tool and outcome |

Letting a model compose tool calls in code it writes buys exactly one thing —
fewer round trips — at the cost of executing that code. Obviously worth it at
fifteen calls a turn; obviously not at two. **Count first, decide after.** The
p95 of `calls_per_turn` on a real workload is the whole argument.

## What a real model changed

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

**Shape is not the same as meaning.** A later run answered `score: 100` out of
five and bound cleanly, because 100 is a perfectly good integer.
`[Range(1, 5)]` now does double duty — it is written into the schema the model
is given, and it is checked after binding:

```csharp
public sealed record Verdict(
    bool Approved,
    [property: Range(1, 5)] int Score,
    string[] Problems);
```

```json
"score":{"type":"integer","minimum":1,"maximum":5}
```

`[MaxLength]` becomes `maxLength` on a string and `maxItems` on a collection —
the same attribute, the right keyword, because a schema the model cannot satisfy
is as bad as a validator that disagrees with it. DataAnnotations rather than a
vocabulary of our own: it is already what a .NET developer reaches for.

**That reasoning was right and incomplete.** Switching the trim analyzer on
showed that `MaxLengthAttribute`'s own *constructor* carries
`[RequiresUnreferencedCode]`, because `ValidationAttribute.IsValid` inspects
arbitrary types. A DataAnnotation is therefore trim-hostile **where it is
written**, not only where it is enforced: `[Range(1, 5)]` on a record makes that
record's assembly unverifiable even when nothing ever reflects over it.

So there are now two vocabularies, and both are read:

```csharp
public sealed record Verdict(
    bool Approved,
    [property: Bounded(1, 5)] int Score,           // no IsValid, nothing to inspect
    [property: Sized(Max = 400)] string Summary,   // characters
    [property: Sized(Min = 1)] string[] Problems); // items
```

`[Bounded]` and `[Sized]` live in `AgentInfer.Abstractions` — netstandard2.0, no
package references — and have no base class and no behaviour. They are facts the
generator reads at compile time and metadata nothing needs at run time.
`[Range]`, `[MinLength]` and `[MaxLength]` still work and are still the right
choice when the assembly is not a trimming target, which is most of them.

Two constructors on `[Bounded]` rather than one taking `double`, because the
bound is emitted into a C# pattern as well as into a schema and
`value.Score is < 1.0` does not compile against an `int`. `[Sized]` takes named
properties because one end is usually absent and `Sized(0, 400)` does not say
which end it bounds; an unset end emits no keyword and no check rather than a
condition that is always true.

One reader serves both families and feeds both the schema and the check. Two
readers would be two chances to disagree about what a bound means, and the
disagreement presents as a model told one thing and held to another. Declaring
both on one property is `AIN015` rather than a precedence rule.

**The model was never told the schema.** With binding enforced, the next run
failed with `missing required properties: 'approved', 'score', 'problems'` and
the reply `{"supported": true}`. Which was a reasonable invention: the JSON path
said *"reply with JSON only"* and never said **which** JSON. Tool parameters had
a compile-time schema; return types did not, so the library's central claim was
half true.

The generator now emits one for the return type as well:

```json
{"type":"object",
 "properties":{"approved":{"type":"boolean"},
               "score":{"type":"integer"},
               "problems":{"type":"array","items":{"type":"string"}}},
 "required":["approved","score","problems"],
 "additionalProperties":false}
```

camelCased to match `JsonSerializerOptions.Web`, because a schema that disagrees
with the binder is worse than none — the model obeys it and the bind fails
anyway. It is used **twice**: set as the provider's `ResponseFormat` where that
is supported, and written into the prompt where it is not. Both, because they
fail in different places.

## The cheaper fix, before reaching for generated code

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

## One thing the counting revealed

Within a single call, **filtering the menu is what enforces a permission**. A
tool the caller may not use is not in `ChatOptions.Tools`, so there is no
`AIFunction` by that name for the loop to invoke — it never reaches the check
inside `GatedFunction`, and the log records zero calls rather than a denial.

The check still earns its place, just not there: it fires when an invoker
outlives a permission change between turns, or when something calls
`InvokeAsync` directly. Worth knowing which of the two gates is load-bearing
where.
