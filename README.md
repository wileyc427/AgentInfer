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

## What this is

⚠️ **Experimental.** The version is below 1.0 and means it: the API can still
change, and it has not been run in anger by anyone but me.

It was inspired by [NOOA](https://github.com/NVIDIA-NeMo/labs-OO-Agents),
NVIDIA's object-oriented agent framework for Python, which makes an agent an
ordinary class instead of a graph, a chain or a pile of configuration. That idea
seemed worth having in C#, where a static type system and a compiler that runs
long before anything ships can carry more of the weight — a prompt that is
missing, a tool parameter that cannot be described, a return type nothing can
bind, all of it can be a build error rather than a surprise in a trace.

I am a full-stack web developer who likes C#. There is far more AI tooling in
Python than in .NET, and rather than wait for that to even out I wanted to put
something into the ecosystem and find out what the idea costs in a language
built around types and compile-time checks. Most of what is here exists because
pointing it at a small local model showed it was needed.

**Code generation and execution are not implemented.** The model picks which
declared tool to call and with what arguments — that is all. It does not write
code, and nothing here runs code a model produced. Adding it would mean adding a
sandbox and a way to opt in visibly, and it may happen; it has not.

## Install

```bash
dotnet add package AgentInfer
```

One reference is the whole install: the generator ships inside the runtime
package under `analyzers/dotnet/cs`. Targets **net8.0** and **net10.0**.

| Package                          | What it is                                                          |
| -------------------------------- | ------------------------------------------------------------------- |
| `AgentInfer`                     | The runtime generated code calls into, with the generator inside it |
| `AgentInfer.Abstractions`        | The attributes. netstandard2.0, zero dependencies                   |
| `AgentInfer.DependencyInjection` | Configuration binding and DI registration                           |

## Where it is

The generator, the attributes, a Predict runtime over
`Microsoft.Extensions.AI`, `[AgentTool]` with compile-time schemas, enforced
`[RequiresPermission]`, a tool-calling loop, `AgentScope` for counting and
bounding a whole workflow, and `IReplyContract` for a typed reply that binds
without reflection — all verified end to end against a stub endpoint.

**There is no sandbox, and nothing here executes model-written code.** Treat
every `[AgentTool]` you declare as reachable by anyone who can influence the
model's input, and gate it accordingly.

The trim and AOT analyzers are on for everything that ships, so the claim that
nothing is discovered at run time is checked by the build rather than asserted
here.

## Documentation

|                                                                                                |                                                                       |
| ---------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- |
| [Prompts and diagnostics](https://github.com/wileyc427/AgentInfer/blob/main/docs/authoring.md) | Prompt files, and the sixteen build errors that check them            |
| [Tools](https://github.com/wileyc427/AgentInfer/blob/main/docs/tools.md)                       | Compile-time schemas, optional arguments, enforced permissions        |
| [Models and roles](https://github.com/wileyc427/AgentInfer/blob/main/docs/models.md)           | Binding a role to a model, and keeping credentials out of the file    |
| [Composing agents](https://github.com/wileyc427/AgentInfer/blob/main/docs/composing.md)        | Chaining, routing, agent-as-tool, and when to write the class by hand |
| [Typed replies](https://github.com/wileyc427/AgentInfer/blob/main/docs/typed-replies.md)       | `IReplyContract`, JSON contexts, anticipated failures                 |
| [Bounding and measuring](https://github.com/wileyc427/AgentInfer/blob/main/docs/measuring.md)  | `AgentScope`, the instruments, and what a real model changed          |
| [Gotchas](https://github.com/wileyc427/AgentInfer/blob/main/docs/gotchas.md)                   | Four things that cost time here                                       |

## Build

Needs the .NET 10 SDK — `global.json` pins it. The library targets net8.0 as
well, so CI installs the net8.0 runtime to run that half of the tests.

```bash
dotnet build
dotnet test
dotnet run --project samples/Incident     # four workflow patterns, no model needed
dotnet run --project samples/Intake       # a hand-written agent over generated tools
dotnet run --project samples/Ledger       # tools, permissions, model roles
```

`samples/Incident` and `samples/Intake` run against a scripted `IChatClient`, so
they work with nothing installed — the runtime takes an `IChatClient` and
nothing else, which makes a workflow's _shape_ testable without paying for a
token. It is not a substitute for a real run: every interesting failure
described in the docs came from pointing this at a small local model, and a
scripted one reproduces none of them. Pass `--live` for that, and `--metrics` to
print the instruments at exit.

`samples/Ledger` needs a real endpoint. See
[Models and roles](https://github.com/wileyc427/AgentInfer/blob/main/docs/models.md) for the configuration it reads, and
[Bounding and measuring](https://github.com/wileyc427/AgentInfer/blob/main/docs/measuring.md) for what the counters are for.

**No credential belongs in a committed file.** Keys come from the environment or
from `appsettings.Development.json`, which is gitignored.

## Contributing

Issues and pull requests are welcome. [`CONTRIBUTING.md`](https://github.com/wileyc427/AgentInfer/blob/main/CONTRIBUTING.md) covers
the part that is not guessable from the tree: the SDK the build pins, the four
things that fail in CI more often than anywhere else, when `<Version>` has to
move, and why the generator targets the compiler versions it does.

To report a vulnerability, see [`SECURITY.md`](https://github.com/wileyc427/AgentInfer/blob/main/SECURITY.md) — privately, please,
rather than in an issue.

## License

MIT. See [`LICENSE`](https://github.com/wileyc427/AgentInfer/blob/main/LICENSE).
