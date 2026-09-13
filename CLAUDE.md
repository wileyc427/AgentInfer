# AgentInfer

Object-oriented agents for .NET: an agent is an interface, its prompt is an
attribute, and a Roslyn generator writes the implementation at build time. The
argument this library makes is that the compiler should catch what a framework
normally discovers at run time — so a feature that cannot be diagnosed at
compile time needs a second look before it goes in.

Published on nuget.org as `AgentInfer`, `AgentInfer.Abstractions` and
`AgentInfer.DependencyInjection`.

## Layout

| Path | |
| --- | --- |
| `src/AgentInfer.Abstractions` | The attributes. netstandard2.0, zero dependencies |
| `src/AgentInfer.Generator` | The incremental generator. netstandard2.0, `IsPackable=false` — it ships inside `AgentInfer` |
| `src/AgentInfer` | The runtime generated code calls into |
| `src/AgentInfer.DependencyInjection` | Configuration binding and DI registration |
| `samples/` | Incident and Intake run scripted; Ledger needs an endpoint |
| `docs/` | Prose. The README is the entry point and stays short |

## Verifying a change

Use the `verify` skill. The short version:

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release                       # runs net8.0 and net10.0
dotnet run --project samples/Incident -c Release
dotnet run --project samples/Intake   -c Release
```

`dotnet build` succeeding is not evidence a packaging change worked. Pack and
open the `.nupkg` — three packaging regressions have shipped past a green build.

## What fails the build

- **`TreatWarningsAsErrors`** and **`EnforceCodeStyleInBuild`** are on. A warning
  is a broken build, and `.editorconfig` makes file-scoped namespaces and
  explicit accessibility errors rather than preferences.
- **Trim and AOT analyzers** are on for everything that ships. Annotate a
  reflective path honestly with `[RequiresUnreferencedCode]`; do not suppress.
- **Package versions live in `Directory.Packages.props`**, never in a `.csproj`.

## Version discipline

`<Version>` in `Directory.Build.props` moves **in the same commit** as any
change under `src/` or to a shipping dependency. `.github/version-guard.sh`
enforces it on pull requests.

The guard ignores `<ItemGroup Label="Test">` in `Directory.Packages.props`,
because test tooling ships nothing. **That label is load-bearing**: a dependency
that does ship, filed under it, goes unwatched.

nuget.org is immutable. A published version can be delisted, never replaced.

## The generator

- **netstandard2.0 and Roslyn 4.14.0**, both deliberate. A generator is loaded
  into the compiler, and targets the *oldest* supported compiler — raising
  either raises the minimum SDK for every consumer.
- **New diagnostics go in `AnalyzerReleases.Unshipped.md`** or the build fails.
  Use the `add-diagnostic` skill; it touches five places.
- **Read the generated output** rather than reasoning about it. The samples set
  `EmitCompilerGeneratedFiles`:
  `samples/Ledger/obj/generated/AgentInfer.Generator/AgentInfer.Generator.AgentGenerator/`

## Tests

xunit.v3 on **Microsoft.Testing.Platform**, not VSTest. The runner is selected
in `global.json`; there is no VSTest adapter package and adding one does
nothing. The trx flag belongs to the test executable, so it goes after a
separator: `dotnet test -- --report-trx`.

A test that proves a regression is caught is worth more than a comment saying
one is possible. When you find an untested invariant, write the test and delete
the sentence.

## Traps that cost time here

`docs/gotchas.md` is the full list. The ones that bite most often:

- **`Directory.Build.props` cannot see `$(TargetFramework)`** — it is imported
  before the project body, so a condition on it silently reads as empty.
  `Directory.Build.targets` is imported after. But `GenerateDocumentationFile`
  must be in **props**, because the SDK derives `DocumentationFile` during props
  evaluation while the switch making XML-comment warnings fatal is read later.
  Wrong file, either direction, fails silently.
- **`IsPackable` is set in the project body**, so it is invisible from props.
- **An XML comment cannot contain two consecutive hyphens.** Writing a command
  line flag in a `.props`/`.targets`/`.csproj` comment makes the file
  unparseable and surfaces as `NU1015` against projects nobody touched. Name
  flags in prose.
- **Multi-targeting changes packaging.** Per-TFM hooks run once per target, and
  `None Update=` matches nothing in the outer build where non-TFM files are
  collected.

## Comments

Comments here explain **why**, not what. Keep the constraint and what breaks
without it; drop the incident that produced it. "Roslyn generators do not chain,
so a context emitted here would be invisible to System.Text.Json's generator"
stays. "Verified, and the error is ..." goes.

The exception is a comment asserting something is untested — that is a live
statement about the suite, not history. Write the test instead.

## Branches

See `CONTRIBUTING.md`. `<type>/<what-it-does>` in kebab-case; `claude/` and
`dependabot/` are reserved for automation.

## Never

- Commit a credential. Keys come from the environment or
  `appsettings.Development.json`, which is gitignored.
- Skip, disable or quarantine a test to get a green build.
- Add a package reference with an inline version.
- Publish to nuget.org by hand — releases come from a `v*` tag through trusted
  publishing, with no stored key.
