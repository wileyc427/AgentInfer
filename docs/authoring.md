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

# The diagnostics are the product

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
