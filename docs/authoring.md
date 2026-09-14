# Prompts, and the diagnostics that check them

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

## What the model actually receives

Two messages. The system prompt from `[Agent]`, then one user message holding
the task prompt from `[Prompt]` with each argument tagged by its parameter name,
in declaration order:

```
[system]  You answer questions about a household ledger.
[user]    Answer the customer's latest message.

          <transcript>
          customer: my order is late
          agent: let me check
          </transcript>

          <message>
          any update?
          </message>
```

Two consequences follow from that layout, and neither is guessable from the
method signature.

**Declaration order decides whether a prompt cache can hit.** Arguments render
in the order they are declared, so consecutive calls share a prefix only up to
the first argument that changed. Put whatever grows first and whatever varies
last. Measured on two consecutive turns of a 40-exchange conversation:

| Declaration | Prefix shared with the previous turn |
| --- | --- |
| `ReplyAsync(string transcript, string message)` | **94.5%** |
| `ReplyAsync(string message, string transcript)` | 5.1% |

Same tokens, same behaviour, same everything — two parameters swapped. On a
provider that bills cached input at a fraction, that is most of the cost of a
long thread. `agentinfer.tokens.cached_input` is the counter that tells you
which one you built.

**A long argument gets the instruction repeated at the end.** Sixty exchanges
of transcript leave the task prompt two thousand characters back, with a closing
tag as the last thing the model reads before generating. Where the rendered
arguments exceed 500 characters the task prompt is stated again after them —
framing at the top, recency at the bottom:

```
Answer the customer's latest message.

<transcript>…</transcript>

<message>any update?</message>

Answer the customer's latest message.
```

Below that size nothing is buried, and repeating `Summarize this.` either side
of the word `hi` reads as a formatting error rather than as emphasis. On a typed
method the restatement goes *before* the JSON rules and the schema, so the last
two things read are what to do and then how to format it.

## Conversations are the caller's, not the library's

There is no conversation here. A generation method is a function call, a C#
signature has nowhere to put history, and nothing persists between calls. That
is a position rather than an omission — but it means multi-turn is something you
build, and there are two ways.

**Pass the context as an argument.** It renders as `<transcript>` above. Simple,
visible in the signature, and subject to both rules in the previous section.
Right for context that is not conversational in the first place: a document to
summarise, retrieved passages, a diff to review.

**Splice real turns with a decorator.** `AgentRunner` takes an `IChatClient` and
nothing else, so a `DelegatingChatClient` sits between it and the provider:

```csharp
sealed class Threaded(IChatClient inner, List<ChatMessage> history) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
    {
        var list = m.ToList();

        // Only the first request of a method call. Tool-loop rounds are longer
        // and already carry their own assistant and tool turns.
        if (list.Count == 2) list = [list[0], .. history, list[1]];

        return base.GetResponseAsync(list, o, ct);
    }
}

var support = new AgentRunner(new Threaded(client, thread.History));
```

This is the better answer for anything genuinely conversational, and not only
because the roles survive. It also moves the bulk out of the user message: the
same sixty exchanges take the final user message from 2,119 characters to 71,
which puts the instruction next to the question rather than two thousand
characters behind it.

Three things to get right:

- **The `Count == 2` guard.** Without it a tool-using method re-prepends the
  whole history on every round of the loop. `PromptLayoutTests` pins both sides
  of that discriminator, so a change to the message shape breaks the build
  rather than your wrapper.
- **Override the streaming method too**, or streamed calls quietly bypass the
  splice.
- **Correlation is yours.** Nothing in a call identifies a conversation. An
  `AsyncLocal` set at the call site flows into the decorator, the same mechanism
  `AgentScope` already uses.

Neither shape fences prompt injection. Text a user wrote is text a user wrote,
whether it arrives inside `<transcript>` or as a `ChatRole.User` turn; the tags
help a model tell instruction from data, and they are not a boundary.

## Laying out a project

Flat files named for what they hold, and one directory:

```
Prompts/ledger-analyst.md   prompt files, read at compile time
LedgerAgents.cs             the [Agent] interfaces and the types they return
LedgerTools.cs              the [AgentTools] class
LedgerJson.cs               the JSON context, if this project needs one
ModelRoles.cs               role names, so they are not string literals
Program.cs                  composition
appsettings.json            providers, and which model serves each role
```

`Prompts/` is the only directory the build knows by name, and only because the
package globs `Prompts/**/*.md` into `AdditionalFiles`. Anything else listed
there works; the glob is a default, not a requirement.

All three samples are this shape, and none of them nests further. A project
with twenty agents would earn `Agents/` and `Tools/`, and the rest of the list
would not change — [the context stays one file](json-contexts.md) however large
the project gets, and most projects have no context at all.

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

# Streaming the reply

A generation method that returns `IAsyncEnumerable<string>` hands the text back
as the model writes it:

```csharp
[Prompt("Draft a postmortem for this incident from these findings.")]
[Model(ModelRoles.Accurate)]
public IAsyncEnumerable<string> StreamDraftAsync(
    string alert, string findings, CancellationToken ct = default);
```

That is the whole declaration. Everything else holds: the model role still
routes, tools still run and are still narrowed by permission, and `AgentScope`
still counts the request — `CountingChatClient` wraps the streaming call as
well as the buffered one, so a bound set for a workflow is not quietly bypassed
by streaming it.

The generated method forwards the runner's enumerable rather than iterating it,
so it stays an ordinary method rather than becoming an iterator and there is no
second enumerator in the middle. **Dispose it.** `await foreach` does; a
hand-rolled loop that returns early does not, and abandoning it leaves the tool
loop's own enumerator open.

**Only `string` streams.** A typed reply is validated as a whole, so there is no
half-bound `Review` worth handing anyone — those methods keep returning
`Task<T>` and complete. `IAsyncEnumerable<Review>` is `AIN003`.

`samples/Incident` streams its postmortem draft, so the path runs in CI rather
than only compiling.

# The diagnostics are the product

Seventeen rules, each a failure that was cheap to hit and expensive to notice,
moved from run time to the build. The corollary is a rule this project tries to
hold: **a feature that cannot be diagnosed at compile time should be questioned
before it is added.**

Fourteen are errors. Three are warnings, because they describe a surface
that works and is probably not what you meant.

| Rule | | |
| --- | --- | --- |
| [`AIN001`](#ain001) | Agent requires a system prompt | Error |
| [`AIN002`](#ain002) | Generation method requires [Prompt] | Error |
| [`AIN003`](#ain003) | Unsupported return type | Error |
| [`AIN004`](#ain004) | Code execution is not implemented | Error |
| [`AIN005`](#ain005) | Unsupported tool parameter | Error |
| [`AIN006`](#ain006) | Tool requires [RequiresPermission] | Warning |
| [`AIN007`](#ain007) | Model requires a role | Error |
| [`AIN008`](#ain008) | Prompt file is not visible to the compiler | Error |
| [`AIN009`](#ain009) | Agent has both a prompt and a PromptFile | Error |
| [`AIN010`](#ain010) | Prompt file matches more than one AdditionalFiles entry | Error |
| [`AIN011`](#ain011) | Flags enum has no JSON schema | Error |
| [`AIN012`](#ain012) | [AgentTools] type has no tools | Warning |
| [`AIN013`](#ain013) | [AgentInferJson] needs a JsonSerializerContext | Error |
| [`AIN014`](#ain014) | Return type is not declared in the JSON context | Error |
| [`AIN015`](#ain015) | Property is bounded twice | Error |
| [`AIN016`](#ain016) | Tool result cannot be rendered | Error |
| [`AIN017`](#ain017) | JSON context disagrees with the binding options | Warning |

### AIN001

**Agent requires a system prompt** · Error

> '{0}' has [Agent] with an empty prompt. The prompt is the only thing the
> model is told about who it is.

An agent with no prompt is not a run-time error. It runs on whatever generic
instruction the runtime supplies, answers plausibly, and is nobody's intent.

Give `[Agent]` a prompt, or point it at a `PromptFile`.

### AIN002

**Generation method requires [Prompt]** · Error

> '{0}' is on an [Agent] interface but has no [Prompt]. Add one, or move the
> method off this interface.

A method with no prompt gets an empty task and behaves almost right, which is
worse than failing.

Add `[Prompt]`, or move the method off the interface.

### AIN003

**Unsupported return type** · Error

> '{0}' returns '{1}'. A generation method must return Task<T>, where T is the
> contract the reply is bound to, or IAsyncEnumerable<string> to receive the
> text as it arrives.

A return type nothing can bind fails on the first call, after the model has
already been paid for.

Return `Task<T>`, where `T` is what the reply binds to — see
[Typed replies](typed-replies.md) — or `IAsyncEnumerable<string>` to stream the
text. `IAsyncEnumerable<T>` of anything but `string` is this error: a typed
reply is validated as a whole, so there is no partial value to hand back.

### AIN004

**Code execution is not implemented** · Error

> '{0}' asks for Strategies.CodeAct, which needs a sandbox this library does
> not provide. Use Predict, which is the default.

Executing model-written code is opt-in here, and not yet available at all.
An error rather than a method that silently does something else — a strategy
that runs generated code should never be what you get by not choosing.

Use `Strategies.Predict`.

### AIN005

**Unsupported tool parameter** · Error

> '{0}' takes '{1} {2}', which has no JSON schema. Use a scalar, an enum, or
> an array of those.

A parameter the schema builder cannot describe. Caught here rather than as a
model sending a shape the parameter cannot take, learned from a trace.

Take a scalar, an enum, or an array of those. See [Tools](tools.md).

### AIN006

**Tool requires [RequiresPermission]** · Warning

> '{0}' is an [AgentTool] with no [RequiresPermission]. Say what a caller must
> hold, even if it is a permission everyone has.

Keeping a method out of the documentation leaves it perfectly callable. Here
every tool states its permission and the generator narrows what is bound, so
"may this caller reach it" is answered before anything runs.

Add `[RequiresPermission("...")]`, naming a permission even if everyone holds
it.

### AIN007

**Model requires a role** · Error

> '{0}' has [Model] with an empty role. Name what the method needs —
> "accurate", "cheap" — not a model id.

A role has to be something a router can be configured for. An empty one
resolves to nothing and presents as a missing registration.

Name a role. See [Models and roles](models.md).

### AIN008

**Prompt file is not visible to the compiler** · Error

> '{0}' names prompt file '{1}'. Add <AdditionalFiles Include="{1}" /> to the
> project; the compiler cannot see a file that is not listed there.

The file is sitting in the project and looks present, which makes this the one
failure here that carries its fix in the message rather than a pointer to a
page.

Paste the line from the message, or move the file under the `Prompts/**/*.md`
glob the package's `buildTransitive` targets already add.

### AIN009

**Agent has both a prompt and a PromptFile** · Error

> '{0}' sets both a prompt and PromptFile '{1}'. Keep one; the other is already
> out of date.

Two sources for one string. One of them is stale and nothing can say which, so
picking a winner would be exactly the silent almost-right behaviour the rest of
these exist to prevent.

Keep one.

### AIN010

**Prompt file matches more than one AdditionalFiles entry** · Error

> '{0}' names prompt file '{1}', which matches {2} files. Make the path
> project-relative so it names one.

Only reachable when the project directory is unknown and the path has to be
matched by suffix. Named separately from `AIN008` because "found too many" and
"found none" have different fixes.

Make the path project-relative.

### AIN011

**Flags enum has no JSON schema** · Error

> '{0}' uses '{1}', which is a [Flags] enum. JSON Schema describes a choice of
> one value, not a set — return an array of a plain enum instead.

The member list describes a choice of one, so a set-valued type offered that
way invites the reply `"Read, Write"` — which reads correctly and binds to
nothing.

Return an array of a plain enum.

### AIN012

**[AgentTools] type has no tools** · Warning

> '{0}' has [AgentTools] but no method carries [AgentTool]. Mark the methods a
> model may call, or drop the attribute.

A type asking to be a tool surface, with no tools on it. The generated invoker
then offers a model nothing, which presents as an agent that answers without
ever calling anything.

Mark the methods a model may call, or drop the attribute.

### AIN013

**[AgentInferJson] needs a JsonSerializerContext** · Error

> '{0}' does not derive from JsonSerializerContext. [assembly: AgentInferJson]
> names the partial class System.Text.Json's generator fills in.

Without this the failure is a cast error inside a generated file you cannot
open.

Point the attribute at a `partial class` deriving from `JsonSerializerContext`.
See [AIN016](#ain016) for the full shape.

### AIN014

**Return type is not declared in the JSON context** · Error

> '{0}' returns '{1}', which '{2}' does not serialize. Add
> [JsonSerializable(typeof({3}))] to it.

The one diagnostic that pays for the whole opt-in. Generators cannot see each
other's output, so a missing `[JsonSerializable]` surfaces as a null
`JsonTypeInfo` on the first call. Reading the context's own attributes turns
that into a line saying which type to add and where.

Add the `[JsonSerializable]` line from the message.

### AIN015

**Property is bounded twice** · Error

> '{0}' carries both an AgentInfer bound and a DataAnnotations one. Keep one:
> [Bounded]/[Sized] are trimmable, [Range]/[MaxLength] are what a .NET developer
> reaches for.

A silent winner between two attributes that each look authoritative is how one
of them ends up stale — the same reading `AIN009` gives a prompt named twice.

Keep one family.

### AIN016

**Tool result cannot be rendered** · Error

> '{0}' returns '{1}', which is not a scalar, an enum or an array of those.
> Declare [assembly: AgentInferJson] with [JsonSerializable(typeof({1}))], or
> return a simpler shape.

An error rather than a reflective fallback, and the asymmetry with the reply
path is deliberate. A reply type is one per method and visible in the
signature; tools are a menu that grows, and a silent fallback is exactly how
the parameter schemas would have rotted if they had not been compile-time from
the start.

Return a simpler shape, or declare a JSON context. A tool result richer than a
scalar, an enum or an array of those has no overload to render it, and the tool
path has no reflective fallback on purpose.

[JSON contexts](json-contexts.md) has the declaration to paste, the options it
must carry, and why the six lines cannot be emitted for you.

### AIN017

**JSON context disagrees with the binding options** · Warning

> '{0}' does not set {1}. AgentInfer's reflective path does, so the same reply
> binds differently depending on which overload a method takes.

A context declares its own options and the reflective path has its own, and
nothing but this connects them. When they disagree the same reply binds one way
through `CompleteJsonReflectivelyAsync` and another through an
`IReplyContract` — which is what the remarks on every context say must not
happen, and what was true of all five in this repository until something
checked. A model answering `"score": "4"` bound on one path and threw on the
other, and a number arriving as a string is the most common thing a model gets
wrong about JSON.

A warning rather than an error. The code compiles and runs; it binds
differently. Making it an error would break every consumer who already declared
a narrower context, for a divergence they may never hit.

The message names only the options that disagree, so it is also the fix.
[AIN016](#ain016) shows the whole declaration.
