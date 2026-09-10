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

**P1 done, P2 started.** The generator, the attributes, a Predict runtime over
`Microsoft.Extensions.AI`, and a real model call verified end to end. `[AgentTool]`
discovery and compile-time schemas are in; the function-calling loop and the
authorization facades are not. No sandbox — that is P3 and it is not started.

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

Still to come in P2: the function-calling loop (`ChatOptions.Tools` and
dispatch) and the per-permission facades that make `[RequiresPermission]`
enforceable rather than declarative.

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
