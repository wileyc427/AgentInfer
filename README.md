# AgentInfer

Object-oriented agents for .NET. An agent is an interface, its prompt is an
attribute, and a Roslyn generator writes the implementation at build time.

```csharp
[Agent("""
    You answer questions about a household ledger.
    Use only figures you were given. Never invent an amount.
    """)]
public interface ILedgerAnalyst
{
    [Prompt("Summarize this spending against its budget in two sentences.")]
    public Task<string> SummarizeAsync(string spending, CancellationToken ct = default);

    [Prompt("Judge whether this summary is supported by the figures given.")]
    public Task<Verdict> ReviewAsync(string summary, string figures, CancellationToken ct = default);
}
```

That is the whole authoring surface. `LedgerAnalystAgent` is generated,
implements the interface, and takes an `AgentRunner`. Consumers inject
`ILedgerAnalyst`, so mocking an agent in a test needs no framework support and
no model.

Design reasoning lives below, next to the code it argues for: the two decisions
worth knowing, why the generator is optional, and what pointing this at a real
model changed.

## Where it is

**P1 and P2 done.** The generator, the attributes, a Predict runtime over
`Microsoft.Extensions.AI`, `[AgentTool]` with compile-time schemas, enforced
`[RequiresPermission]`, a tool-calling loop, `AgentScope` for counting and
bounding a whole workflow, and `IReplyContract` for a typed reply that binds
without reflection — all verified end to end against a stub endpoint. No sandbox
and no CodeAct: that is P3 and it is not started.

The trim and AOT analyzers are on for everything that ships, so the claim that
nothing is discovered at run time is checked by the build rather than asserted
here. It was asserted here for a long time and was not true; see the third
gotcha.

**Built against.** A private application uses it for an agent over World of
Warcraft capture data — thirteen tools with compile-time schemas, a prompt file,
a typed reply, and a permission that gates nothing yet and is declared anyway.
Optional tool parameters exist because that consumer needed them; see the commit,
and the rule below about features that cannot be diagnosed at compile time.

| Package | What it is |
| --- | --- |
| `AgentInfer.Abstractions` | The attributes. netstandard2.0, zero dependencies |
| `AgentInfer.Generator` | The Roslyn incremental generator. Not published on its own — it ships inside `AgentInfer` under `analyzers/dotnet/cs`, so one `PackageReference` is the whole install |
| `AgentInfer` | The runtime generated code calls into |

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
| `AIN001` Agent requires a prompt | an f-string is not a docstring, so you silently inherit the framework's internal prompt |
| `AIN002` Method requires `[Prompt]` | an empty task prompt; the method behaves almost right |
| `AIN003` Must return `Task<T>` | a return annotation the strategy cannot satisfy, discovered after paying for a call |
| `AIN004` CodeAct is not implemented | an undecorated method silently executing generated code |
| `AIN005` Unsupported tool parameter | a model sending a shape the parameter cannot take, learned from a trace |
| `AIN006` Tool requires `[RequiresPermission]` | `@hidden`, which keeps a method out of the docs and leaves it callable |
| `AIN007` `[Model]` requires a non-empty role | a role that silently resolves to nothing and routes to the default model |
| `AIN008` Prompt file is not in `AdditionalFiles` | a prompt file the compiler cannot see, sitting visibly in the project |
| `AIN009` Both a prompt and a `PromptFile` | two sources for one string, one of them stale, neither obviously the winner |
| `AIN010` Prompt file matches more than one entry | a path that names two files and picks one of them quietly |
| `AIN011` Flags enum has no schema | `"Read, Write"` — a reply that reads correctly and binds to nothing |
| `AIN012` `[AgentTools]` with no tools | an invoker that offers a model nothing, read as an agent that never calls one |
| `AIN013` `[AgentInferJson]` is not a context | a cast error inside a generated file you cannot open |
| `AIN014` Return type not serialized | a null `JsonTypeInfo` on the first call |
| `AIN015` Property bounded twice | two values under one schema keyword, silently |
| `AIN016` Tool result cannot be rendered | a reflective serializer, one line below the typed binding |

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
<AgentInferIncludePromptFiles>false</AgentInferIncludePromptFiles>

<!-- or point it somewhere else -->
<AgentInferPromptFiles>Agents/**/*.prompt</AgentInferPromptFiles>
```

Anything outside that glob needs a line in the project file, and `AIN008` says
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

The same was not true of the **result** for far longer than it should have
been. Arguments came in through compile-time schemas and compile-time accessors,
and then the return value went back out through
`JsonSerializer.Serialize<T>(result)` — one line below, in the same generated
`switch`. It survived because nothing checked. See
[Rendering a tool result](#rendering-a-tool-result).

Tools are opt-in one method at a time. A public method without `[AgentTool]` is
**absent** from the manifest, not hidden from documentation while remaining
callable — which is what NOOA's `@hidden` actually does.

### An argument the model may leave out

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

## Two methods, two models

```csharp
[Prompt("Which categories are over budget, and by how much?")]
[Model("accurate")]
public Task<string> SummarizeAsync(CancellationToken ct = default);

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
There is no `"accurate"` → `"claude-sonnet-5"` table inside AgentInfer — that
string is deployment configuration.

```json
{
  "AgentInfer": {
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
services.AddAgentInferModels(configuration, (binding, sp) => ClientFor(binding))
        .ValidateRoles(AgentInferRoles.All);
```

**Roles can live on different providers.** A local model for classification and
a hosted one for the method that has to reason is the point of per-method
models, and it does not work if every role shares one endpoint.

A role may be a **bare string**, meaning the default provider — most apps have
one, and making them write an object to say so would be a tax on the common
case. With exactly one provider configured, `DefaultProvider` is optional too.

`AddAgentInferModels` registers one keyed `AgentRunner` per role plus an
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
public Task<string> SummarizeAsync(CancellationToken ct = default);
```

`const`, because an attribute argument must be a compile-time constant. A rename
is then a rename.

**These are yours to define, not generated** — and that is a constraint rather
than an omission. The generator learns a role *by reading the attribute*, so a
constant it emitted could not be used in the attribute that produced it. The
dependency only runs one way.

What *is* generated is `AgentInferRoles.All`, the set of roles actually asked for:

```csharp
internal static class AgentInferRoles
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
summarized correctly once and answered *"no categories are over budget"* the
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
honoring `[JsonStringEnumMemberName]` where it is used, because a schema that
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

## The generator is optional, and the split is uneven

An agent is an interface, so the implementation is a class — and you can write
that class. Nothing in the runtime requires generated code: `AgentRunner`,
`ToolInvoker`, `IModelRouter` and `AgentScope` have no idea whether their caller
was generated.

Worth knowing what you would be replacing. `LedgerAnalystAgent.g.cs` is 136
non-comment lines for two methods and five tools:

| | Lines | Worth hand-writing? |
| --- | --- | --- |
| Prompt constant, constructor, two call sites | ~45 | Yes. Mechanical, and it does not rot |
| `ToolManifest` — five JSON Schema strings | ~30 | Derived from the method signatures |
| Dispatch switch — typed argument binding | ~45 | Derived from the method signatures |
| `ResponseSchema` for `Verdict` | one string | Derived from the record and its `[Range]` |

The value is in the derived two-thirds. And the diagnostics agree: AIN001,
AIN002, AIN004, AIN007 and AIN008–010 police hazards that **only exist because
of the attribute surface** — write the class and the hazard and the rule
disappear together. The rules that survive into a hand-written world are AIN005,
AIN006 and AIN011, which are the schema and permission rules. The two arguments
land in the same place, which is why there is an attribute for taking the tools
half alone:

```csharp
[AgentTools]
public sealed class IntakeTools
{
    [AgentTool("The full text of one ticket, with the customer and their plan.")]
    [RequiresPermission("intake.read")]
    public string Ticket(string id) => ...;
}

// Generated: IntakeToolsInvoker, with IntakeToolsInvoker.Tools as the manifest.
var tools = new IntakeToolsInvoker(new IntakeTools());
IIntake intake = new IntakeAgent(router, tools, caller, policy);   // yours
```

One reader and one emitter serve both ways in. Two copies would start identical
and diverge on the first thing either learned, and the symptom is a manifest
that disagrees with the switch serving it — a tool the model is offered and
cannot call, or one it can call that nothing declared a permission for.

### When to write the class

Not as a preference. An attribute argument must be a compile-time constant, so
these cannot be declared at all:

- **A prompt assembled at run time** — from a tenant's policy, a row in a table,
  something that changes without a deploy. `[Agent("…")]` takes a constant and
  `PromptFile` takes a file read at compile time.
- **A model role chosen from the input.** `[Model]` names one role for every
  call to a method. A short ticket and a long one with three complaints in it
  are different problems.
- **A retry that feeds a binding failure back to the model.** The library will
  not do this for you on purpose — a retry hidden inside what looks like one
  call is the same class of surprise as a strategy that silently executes
  generated code. It belongs in the caller's own loop, with the bound visible.

`samples/Intake` is all three, and it states its own costs rather than only its
benefits: you write the `IReplyContract` — schema, `JsonTypeInfo` and value rule
— for every return type, and nothing checks the schema still matches the record.
`AgentInferRoles.All` is replaced by an array somebody maintains, so a role used in
code but missing from it validates clean and fails on the call that needs it.

What it does **not** cost is anything to do with trimming. The hand-written
agent carries no `[RequiresUnreferencedCode]` and suppresses nothing, because
the contract binds through a `JsonTypeInfo` and checks its own values — the same
mechanism the generated path uses.

```bash
dotnet run --project samples/Intake
```

## The typed reply is one object

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

### You declare the context; the generator points at it

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
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

### An anticipated failure is returned, not thrown

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

### Rendering a tool result

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
| `agentinfer.workflow.operations` | histogram — generation method calls per scope |
| `agentinfer.workflow.requests` | histogram — requests actually sent |
| `agentinfer.workflow.duration` | histogram — seconds |

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

## Measuring whether you need generated code

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
dotnet run --project samples/Intake       # a hand-written agent over generated tools
dotnet run --project samples/Ledger       # tools, permissions, model roles
```

`samples/Incident` and `samples/Intake` run against a scripted `IChatClient` by
default, so they work with nothing installed. That is one class, because the runtime takes an
`IChatClient` and nothing else — no HTTP, no provider SDK, no key — which makes
a workflow's *shape* testable without paying for a token. It is not a substitute
for a real run: every interesting failure described above came from pointing
this at qwen3, and a scripted model reproduces none of them, because it is not
trying to be helpful. Pass `--live` for that.

`samples/Ledger` needs a real endpoint.

The sample reads `samples/Ledger/appsettings.json`:

```json
{
  "AgentInfer": {
    "DefaultProvider": "local",
    "DefaultModel": "qwen3:latest",

    "Providers": {
      "local":  { "Endpoint": "http://localhost:11434/v1" },
      "openai": { "Endpoint": "https://api.openai.com/v1", "ApiKeyVariable": "OPENAI_API_KEY" }
    },

    "Models": {
      "smallllm": { "Provider": "local", "Model": "qwen3:latest" }
    }
  }
}
```

A **provider** is an address and where its key lives; a **model role** is a name
the code asks for, bound to a model on one of those providers. They are separate
because several roles usually sit on one endpoint, and duplicating an endpoint
per role is how two of them end up disagreeing.

**The samples invert the usual precedence: a value in `appsettings` beats one in
the environment.** That is not how a production host should be wired and it is
right here — you edit a file, run, and what you typed is what runs, rather than
losing to an export from an hour ago that nothing on screen mentions. Each run
prints the winner and where it came from, so a surprise is one line away rather
than an investigation.

"Beats" means a value that is **present** wins. A key absent from the file still
falls through to the environment, which is what stops a committed file blanking
a real credential. A key you want in a file goes in
`appsettings.Development.json`, which is gitignored.

Two flags worth knowing while testing:

```bash
dotnet run --project samples/Incident -- --live --metrics
```

`--metrics` subscribes to the `AgentInfer` meter and prints each instrument's
distribution at exit. Nothing listens to a `Meter` by default, so without it
`agentinfer.tool.calls_per_turn` — the number that decides whether a workload ever
needs generated code — is recorded into a void. A real host points
OpenTelemetry at the meter instead.

Point the `smallllm` role at something larger to give `SummarizeAsync` a better
model while everything else stays put — switch its `Provider` to `openai` and its
`Model` to whatever you want, and the `ApiKeyVariable` already declared there
says where the key comes from.

Environment variables **fill in what the file omits**, rather than overriding it:
`AGENTINFER__MODELS__SMALLLLM__MODEL` is read only if `Models:smallllm:Model` is
absent from `appsettings.json`. That follows from the inverted precedence above,
and it is the direction that matters — a committed file cannot blank a credential
the environment supplies, and it also cannot be quietly redirected by an export
you have forgotten about.

**No credential lives in that file.** It is committed, and a plausible-looking
value in a committed file is one somebody pastes a real key over. Keys come from
the environment or user-secrets.

The sample sets `EmitCompilerGeneratedFiles`, so what the generator produced is
readable at
`samples/Ledger/obj/generated/AgentInfer.Generator/AgentInfer.Generator.AgentGenerator/`.
Reading it is the fastest way to understand the library, and it is how the
string-routing bug in the first draft was found.

## Four gotchas that cost time here

**Analyzers do not flow transitively through `ProjectReference`.** `AgentInfer`
references the generator as an `Analyzer`, but a project referencing `AgentInfer`
gets the runtime and no generator. It works through a NuGet package; inside
this repo every consuming project references the generator again explicitly.
See `samples/Ledger/Ledger.csproj`.

**`Directory.Build.props` cannot see `$(TargetFramework)`.** It is imported
before the project body, so any condition on the target framework silently
evaluates against an empty string. `IsAotCompatible`, `EnableTrimAnalyzer` and
`EnableSingleFileAnalyzer` sat in that file, conditioned that way, and were
never once set — so nothing in this repo verified the claim that nothing is
discovered at run time. Switching them on found fourteen violations in
`GatedFunction.cs`, the file this README holds up as the reflection-free path.
They live in `Directory.Build.targets` now, which is imported after the project
body. The failure leaves no trace anywhere: no error, no warning, just a
property that is quietly empty.

**The generator must target netstandard2.0**, because it is loaded into the
compiler. Anything else produces an analyzer the SDK declines to load, with no
error anywhere — it presents as "my generator produced nothing".

**Never compare rendered type names.** `SymbolDisplayFormat.FullyQualifiedFormat`
keeps the `string` keyword alias rather than expanding it to `System.String`,
so a display-string comparison silently fails. Use `SpecialType`. The first
draft did it the wrong way, compiled cleanly, and JSON-encoded every string
argument into its own prompt.

## Contributing

Issues and pull requests are welcome. [`CONTRIBUTING.md`](CONTRIBUTING.md) covers
the part that is not guessable from the tree: the SDK the build pins, the four
things that fail in CI more often than anywhere else, when `<Version>` has to
move, and why the generator targets the compiler versions it does.

To report a vulnerability, see [`SECURITY.md`](SECURITY.md) — privately, please,
rather than in an issue.

## License

MIT. See [`LICENSE`](LICENSE).
