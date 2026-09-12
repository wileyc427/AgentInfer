# Composing agents

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

## Routing wants a closed return type

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

## One agent as another agent's tool

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

# The generator is optional, and the split is uneven

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

## When to write the class

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
