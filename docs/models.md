# Models and roles

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

## Where a role becomes a model name

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

## The credential is never in the file

`ApiKeyVariable` names the environment variable holding the key. It is not the
key, and there is no field that is. A committed file with a key-shaped field is
a file somebody eventually puts a real key in, and a plausible-looking value in
a committed example is one somebody pastes a real one over.

## Role names without magic strings

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
