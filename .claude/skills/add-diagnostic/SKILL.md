---
name: add-diagnostic
description: Add a new AIN#### compile-time diagnostic to the AgentInfer Roslyn generator. Use when the generator should reject something at build time instead of failing at run time. Covers the descriptor, the release tracking file the build requires, reporting it without breaking incrementality, the test, and the docs table.
---

# Add a generator diagnostic

The diagnostics are the product. Prefer one to a runtime failure — a feature
that cannot be diagnosed at compile time needs a second look before it goes in.

Five places. Missing the second fails the build; missing the last two leaves the
rule undiscoverable.

## 1. The descriptor

`src/AgentInfer.Generator/Diagnostics.cs`. Take the next free id in the `AIN`
sequence — never reuse one, and never change what an existing id means. Match
the surrounding style: say what is wrong and what to do instead.

## 2. Release tracking

`src/AgentInfer.Generator/AnalyzerReleases.Unshipped.md`, one row:

```
AIN0NN  | AgentInfer | Error | What it rejects
```

`EnforceExtendedAnalyzerRules` fails the build without it. This is deliberate: a
rule id that changes meaning between versions silently reclassifies somebody's
build. Rows move to `AnalyzerReleases.Shipped.md` when a version ships.

## 3. Report it

Diagnostics ride alongside the model rather than being reported from
`Transform`. `Transform`'s output is cached, so a diagnostic reported there
disappears the second time an unchanged file is analyzed. Follow what
`AgentGenerator.Result` already does.

## 4. Test it

`tests/AgentInfer.Generator.Tests/`, next to the existing diagnostic tests — see
`DiagnosticTests.cs` and `PromptFileTests.cs` for the shape. Assert the id on a
source that should be rejected, and assert a valid source still produces no
diagnostic. `GeneratorHarness.Run` drives the generator.

## 5. Document it

The diagnostics table in `docs/authoring.md`. Say what the rule prevents, not
just what it checks — the table's value is that a reader recognises their own
bug in it.

## Then

Run the `verify` skill. A new diagnostic touches the generator, so
`<Version>` moves in the same commit.
