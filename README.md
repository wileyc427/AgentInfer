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
`[RequiresPermission]`, a tool-calling loop, and `AgentScope` for counting and
bounding a whole workflow — all verified end to end against a stub endpoint. No
sandbox and no CodeAct: that is P3 and it is not started.

| Package | What it is |
| --- | --- |
| `Agentry.Abstractions` | The attributes. netstandard2.0, zero dependencies |
| `Agentry.Generator` | The Roslyn incremental generator. Not published on its own — it ships inside `Agentry` under `analyzers/dotnet/cs`, so one `PackageReference` is the whole install |
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
through to a base class's prompt the way a Python docstring silently can. A
prompt too long to want to live in an attribute goes in a file and is still a
constant — see [Prompts in files](#prompts-in-files).

## The diagnostics are the product

| Rule | Replaces |
| --- | --- |
| `AGT001` Agent requires a prompt | an f-string is not a docstring, so you silently inherit the framework's internal prompt |
| `AGT002` Method requires `[Prompt]` | an empty task prompt; the method behaves almost right |
| `AGT003` Must return `Task<T>` | a return annotation the strategy cannot satisfy, discovered after paying for a call |
| `AGT004` CodeAct is not implemented | an undecorated method silently executing generated code |
| `AGT005` Unsupported tool parameter | a model sending a shape the parameter cannot take, learned from a trace |
| `AGT006` Tool requires `[RequiresPermission]` | `@hidden`, which keeps a method out of the docs and leaves it callable |
| `AGT007` `[Model]` requires a role | an empty role, which presents as a missing registration somewhere else |
| `AGT008` Flags enum has no schema | `"Read, Write"` — a reply that reads correctly and binds to nothing |
| `AGT007` `[Model]` requires a non-empty role | a role that silently resolves to nothing and routes to the default model |
| `AGT008` Prompt file is not in `AdditionalFiles` | a prompt file the compiler cannot see, sitting visibly in the project |
| `AGT009` Both a prompt and a `PromptFile` | two sources for one string, one of them stale, neither obviously the winner |
| `AGT010` Prompt file matches more than one entry | a path that names two files and picks one of them quietly |

Each row is a real failure from building against NOOA, moved from production to
the build. The corollary is a rule this repo tries to hold: **a feature that
cannot be diagnosed at compile time should be questioned before it is added.**

## Prompts in files

A system prompt grows. At some length it wants markdown, and a raw string
literal inside an attribute stops being the right home for it:

```csharp
[Agent(PromptFile = "Prompts/ledger-analyst.md", Tools = typeof(LedgerTools))]
public interface ILedgerAnalyst { ... }
```

The generator reads the file **during compilation** and emits the same constant
an inline prompt produces. The generated file says where the text came from:

```csharp
// Prompt read at compile time from: Prompts/ledger-analyst.md
public sealed partial class LedgerAnalystAgent : ILedgerAnalyst
{
    private const string SystemPrompt = @"You answer questions about a household ledger.
    ...
```

So nothing is opened at run time, and trimming, AOT, and *the prompt in the
binary is the prompt that ran* all hold exactly as they do for an inline prompt.
**This buys authoring, not deployment.** Changing a prompt is still a recompile.
If what you want is tuning prompts without a redeploy, this is not that feature,
and that feature trades away the audit property above.

The compiler only sees files listed in `AdditionalFiles`. The package ships a
`buildTransitive` targets file that adds `Prompts/**/*.md` for you:

```xml
<!-- opt out entirely -->
<AgentryIncludePromptFiles>false</AgentryIncludePromptFiles>

<!-- or point it somewhere else -->
<AgentryPromptFiles>Agents/**/*.prompt</AgentryPromptFiles>
```

Anything outside that glob needs a line in the project file, and `AGT008` says
so with the line to paste. Paths in the attribute are relative to the project
directory; the generator resolves them against `ProjectDir`, which the SDK
already makes visible to analyzers.

Two deliberate limits:

- **Method prompts and tool descriptions stay in attributes.** They are
  one-liners that belong next to the signature they describe. Splitting the
  prompt surface across a file *and* the attributes would mean inventing a
  sectioned file format, which means a parser and a diagnostic for every
  missing section.
- **No templating.** Arguments reach the model as separate values appended to
  the user message, never spliced into the system prompt — which is why an
  argument cannot rewrite the agent's instructions. Prompt files do not change
  that, and `{{placeholder}}` is a substantially larger commitment than file
  I/O.

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

## Two methods, two models

```csharp
[Prompt("Which categories are over budget, and by how much?")]
[Model("accurate")]
public Task<string> SummariseAsync(CancellationToken ct = default);

[Prompt("Classify how urgent this request is.")]
public Task<Urgency> TriageAsync(string request, CancellationToken ct = default);
```

generates two different call sites in one class:

```csharp
return await _router.For(@"accurate").CompleteWithToolsAsync(call, …);
return await _runner.CompleteJsonWithToolsAsync<Urgency>(call, …);
```

**It names a role, not a model.** `[Model("accurate")]`, never
`[Model("claude-sonnet-5")]`. A domain assembly should not carry vendor model
ids: the mapping differs between a laptop and production, changes when a model
is deprecated, and is configuration rather than design. Same instinct as
`[RequiresPermission("ledger.read")]` naming a permission rather than a list of
people.

The router is a constructor dependency **only when some method asks for a
role**, so the common case stays one dependency and a router in a constructor is
a signal rather than boilerplate.

### Where a role becomes a model name

Two hops, and the library owns only one. A role resolves to an `AgentRunner`;
the runner already knows its model, because the `IChatClient` was built with it.
There is no `"accurate"` → `"claude-sonnet-5"` table inside Agentry — that
string is deployment configuration.

```json
{
  "Agentry": {
    "DefaultProvider": "local",
    "Providers": {
      "local":  { "Endpoint": "http://localhost:11434/v1" },
      "openai": { "Endpoint": "https://api.openai.com/v1", "ApiKeyVariable": "OPENAI_API_KEY" }
    },
    "Models": {
      "accurate": { "Provider": "openai", "Model": "gpt-5-mini" },
      "cheap": "qwen3:latest"
    }
  }
}
```

```csharp
services.AddAgentryModels(configuration, (binding, sp) => ClientFor(binding))
        .ValidateRoles(AgentryRoles.All);
```

**Roles can live on different providers.** A local model for classification and
a hosted one for the method that has to reason is the point of per-method
models, and it does not work if every role shares one endpoint.

A role may be a **bare string**, meaning the default provider — most apps have
one, and making them write an object to say so would be a tax on the common
case. With exactly one provider configured, `DefaultProvider` is optional too.

`AddAgentryModels` registers one keyed `AgentRunner` per role plus an
`IModelRouter` over them. Clients are built **lazily and once**, so registering
ten models opens no connections.

It does not build clients itself. Constructing an `IChatClient` is
provider-specific — SDK, credential type, options — and a library that guessed
would be wrong for everyone but its author. The factory receives a
`ModelBinding` carrying the role, the model and the resolved provider, because a
model name means nothing without an endpoint: `gpt-5-mini` against a local
Ollama is a 404 that reads as a missing model rather than a misrouted request.

Four things fail at **registration** rather than on first use: a role with no
model, a role naming a provider that is not configured, a provider with no
endpoint, and — with several providers — a role that names none while
`DefaultProvider` is unset. A provider whose `ApiKeyVariable` is unset is a
warning, since a key can arrive from somewhere the configuration cannot see.

### The credential is never in the file

`ApiKeyVariable` names the environment variable holding the key. It is not the
key, and there is no field that is. A committed file with a key-shaped field is
a file somebody eventually puts a real key in — the same mistake as the
working-looking IP address in the Python side's example env file, which sent
every request to a machine that was not running anything.

### Role names without magic strings

Define your own constants and use them in the attribute:

```csharp
public static class ModelRoles
{
    public const string Accurate = "accurate";
}

[Model(ModelRoles.Accurate)]
public Task<string> SummariseAsync(CancellationToken ct = default);
```

`const`, because an attribute argument must be a compile-time constant. A rename
is then a rename.

**These are yours to define, not generated** — and that is a constraint rather
than an omission. The generator learns a role *by reading the attribute*, so a
constant it emitted could not be used in the attribute that produced it. The
dependency only runs one way.

What *is* generated is `AgentryRoles.All`, the set of roles actually asked for:

```csharp
internal static class AgentryRoles
{
    public static readonly string[] All = ["accurate"];
}
```

Your constants make a rename a rename. That array makes a missing registration a
**startup failure** rather than a request that dies halfway through, minutes
after deploy, reading as a missing service. A configured role nothing asks for
is a warning instead — dead configuration is worth noticing and not worth
refusing to start over.

Why it exists: given the same correct one-call tool result, `qwen3:latest`
summarised correctly once and answered *"no categories are over budget"* the
next time — with coffee at 22.80 against a 15.00 budget. Fetching the data was
never the hard part, so the method that has to reason wants a different model
from the one that classifies.

## Composing agents

The agentic workflow patterns — prompt chaining, routing, parallel sectioning,
evaluator-optimizer, orchestrator-workers — need **no framework surface here**,
and that is the strongest thing an interface-shaped agent buys you. An agent is
an interface, so composing agents is composing interfaces, which is a thing the
language already does well.

```csharp
// routing
var outcome = await triage.SeverityAsync(alert) switch
{
    Severity.Ignore      => "No action.",
    Severity.Investigate => await SweepAsync(alert),
    Severity.Page        => $"Paging {await triage.OwnerAsync(alert)}. " + await SweepAsync(alert),
};

// parallel sectioning
var findings = await Task.WhenAll(services.Select(s => investigator.InvestigateAsync(s)));

// evaluator-optimizer
var draft = await postmortem.DraftAsync(alert, evidence);

for (var attempt = 1; attempt <= 3; attempt++)
{
    var review = await postmortem.ReviewAsync(draft, evidence);
    if (review.Approved) break;

    draft = await postmortem.ReviseAsync(draft, string.Join("\n", review.Problems));
}
```

`samples/Incident` runs all four, and runs with nothing installed:

```bash
dotnet run --project samples/Incident            # scripted model
dotnet run --project samples/Incident -- --live  # a real endpoint
```

A declarative surface for these — `[Route]`, a pipeline builder, a graph — would
buy a picture. It would cost a file that steps in a debugger, a bound that is a
number on a line somebody typed, and a stack trace instead of a graph node id.
The rule above settles it: **a workflow graph is no more checkable at compile
time than a `for` loop, and strictly harder to read.**

### Routing wants a closed return type

`Task<Severity>`, not `Task<string>`. The model's options and the caller's
branches are then the same list, checked by the compiler — add a member and
every `switch` over it stops compiling until somebody decides what the new case
does, which is the review step a string answer silently skips.

The generator emits the members, so the model is told which words are legal:

```json
{"type":"string","enum":["ignore","investigate","page"]}
```

camelCased to match the `JsonStringEnumConverter` the runtime binds with, and
honouring `[JsonStringEnumMemberName]` where it is used, because a schema that
named values the binder rejects is worse than no schema at all.

Two smaller things fall out of building it. A scalar schema is **not** sent to
the provider as a response format — OpenAI-compatible structured output requires
an object at the root and rejects `{"type":"string"}` outright, so sending it
turns a call that would have worked into a 400. The prompt still carries it,
which for a closed set of words is the half that was doing the work. And a bare
`page` binds as readily as `"page"`: told to reply with JSON and given a list of
words, a model answers with the word about as often as with the quoted word,
because the quotes look like formatting.

### One agent as another agent's tool

There is no feature for this, which is the point:

```csharp
public sealed class Investigators(IServiceInvestigator investigator)
{
    [AgentTool("Investigate one service and report what its telemetry shows.")]
    [RequiresPermission("incident.read")]
    public async Task<string> Investigate(string service, CancellationToken ct = default)
    {
        using var scope = AgentScope.Begin($"investigate:{service}");
        var finding = await investigator.InvestigateAsync(service, ct);

        return $"{finding.Service}: {(finding.Healthy ? "healthy" : "UNHEALTHY")} — {finding.Evidence}";
    }

    [AgentTool("Wake the on-call engineer for a team.")]
    [RequiresPermission("incident.page")]
    public string Page(Team team, string why) => ...;
}

[Agent("You are the incident commander. ...", Tools = typeof(Investigators))]
public interface IIncidentCommander { ... }
```

A tool may already return `Task<T>`, so a tool whose body starts another agent
is a tool like any other. Orchestrator-workers needs a class, not a registry.

The permission story comes along unchanged and is better here than it looks in a
ledger: `Page` wakes a human at three in the morning, and a commander whose
caller does not hold `incident.page` is never told that paging exists.

Worth comparing against `Task.WhenAll` rather than admiring on its own.
Sectioning knows the work in advance, costs exactly what the list says, and
cannot investigate something nobody listed. A commander decides at run time —
and pays for the deciding, in requests you cannot predict and in a sub-agent
whose calls are invisible from the call site. Which is why:

## Bounding and counting a whole workflow

```csharp
using var scope = AgentScope.Begin("incident.triage", maxRequests: 20);

var severity = await triage.SeverityAsync(alert);
var findings = await Task.WhenAll(services.Select(s => investigator.InvestigateAsync(s)));

logger.LogInformation("{Scope}", scope);
// incident.triage: 6 operation(s), 14 request(s), 4 tool call(s) in 0.2s
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
| `agentry.workflow.operations` | histogram — generation method calls per scope |
| `agentry.workflow.requests` | histogram — requests actually sent |
| `agentry.workflow.duration` | histogram — seconds |

Scopes nest and counts roll up, so an orchestration cannot look cheap by hiding
its work one level down. Per-call spans nest under the scope's span, under the
same `Agentry` source.

Two decisions in there worth stating, because both went the less obvious way.

**The counter is a `DelegatingChatClient` the runner puts around its own
client**, not something a host registers. An `AddAgentryBudget()` a consumer
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
> `Dispose` cannot tell. On a host where `AddAgentryModels` shares one client
> per role, the second call through that role fails. Ownership now stops at the
> decorator: a runner is handed a client, it does not create one, and it must
> not close one.

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
dotnet run --project samples/Incident     # four workflow patterns, no model needed
dotnet run --project samples/Ledger       # tools, permissions, model roles
```

`samples/Incident` runs against a scripted `IChatClient` by default, so it works
with nothing installed. That is one class, because the runtime takes an
`IChatClient` and nothing else — no HTTP, no provider SDK, no key — which makes
a workflow's *shape* testable without paying for a token. It is not a substitute
for a real run: every interesting failure described above came from pointing
this at qwen3, and a scripted model reproduces none of them, because it is not
trying to be helpful. Pass `--live` for that.

`samples/Ledger` needs a real endpoint.

The sample reads `samples/Ledger/appsettings.json`:

```json
{
  "Agentry": {
    "Endpoint": "http://localhost:11434/v1",
    "DefaultModel": "qwen3:latest",
    "Models": { "accurate": "qwen3:latest" }
  }
}
```

Point `accurate` at something larger to give `SummariseAsync` a better model
while everything else stays put. Environment variables layer on top
(`AGENTRY__MODELS__ACCURATE`), so a run can be redirected without editing a
committed file.

**No credential lives in that file.** It is committed, and a plausible-looking
value in a committed file is one somebody pastes a real key over. Keys come from
the environment or user-secrets.

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
