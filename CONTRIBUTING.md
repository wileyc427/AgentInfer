# Contributing

Issues and pull requests are welcome. This file is the part that is not
guessable from the tree.

## What you need

The **.NET 10 SDK**. `global.json` pins `10.0.100` with `rollForward:
latestFeature`, so a 10.0.x SDK works and a 9.x one does not — it fails at
restore with a message about the SDK version rather than about your change.

Nothing else. No endpoint, no API key, no local model: the tests and two of the
three samples run against a scripted `IChatClient`.

```bash
dotnet build
dotnet test
dotnet run --project samples/Incident    # four workflow patterns, no model needed
dotnet run --project samples/Intake      # a hand-written agent over generated tools
```

`samples/Ledger` is the exception and needs a real endpoint. See the Build
section of the README.

## Branches

```
<type>/<what-it-does>
```

Lowercase, kebab-case, describing the change rather than a ticket number.

| Type | |
| --- | --- |
| `feat` | new capability |
| `fix` | a defect |
| `docs` | prose only, including `docs/` and the README |
| `ci` | the workflow, the version guard, dependabot |
| `deps` | dependency moves that are not otherwise a fix |
| `refactor` | behaviour unchanged |
| `chore` | everything else |

So `fix/prompt-file-matches-two-entries`, not `fix/issue-42` and not
`adam/wip`.

Two prefixes are reserved for automation and should not be used by hand:
`claude/` for agent sessions and `dependabot/` for the bot.

The reason to care is that the name outlives the branch. GitHub puts it in the
merge commit subject, where it becomes the permanent record of why a change
landed — `Merge pull request #7 from wileyc427/claude/focused-hypatia-u8z8he`
says nothing that a reader a year later can use. A branch named for its change
produces a merge commit that reads as a sentence.

CI builds every branch, so the name has no effect on what runs.

## Before you open a pull request

CI runs ubuntu and windows, Debug and Release, and runs the two scripted samples
rather than only compiling them. Four things fail there more often than
anywhere else, and all four are cheap to check first:

- **`TreatWarningsAsErrors` is on.** A warning is a broken build, on every cell
  of that matrix.
- **`EnforceCodeStyleInBuild` is on**, against `.editorconfig`. File-scoped
  namespaces and explicit accessibility modifiers are errors, not preferences.
- **The trim and AOT analyzers are on for everything that ships.** If you add a
  path that reflects over a type, the build says so. Annotate it honestly
  (`[RequiresUnreferencedCode]`) rather than suppressing it — there is already
  one annotated path, and the README explains why it is annotated instead of
  hidden.
- **Package versions live in `Directory.Packages.props`, never in a `.csproj`.**
  Central package management is on. With a source generator in the tree this
  matters more than usual: the generator and its tests must agree on the Roslyn
  version exactly, and two project files drifting apart produces errors that
  look like generator bugs.

## Raising the version

`<Version>` in `Directory.Build.props` moves **in the same commit** as any
change under `src/`.

This is not bookkeeping. `dotnet nuget push --skip-duplicate` treats an
already-published version as success, so a change that ships without a bump
publishes nothing, reports green, and leaves every consumer restoring the
previous assembly. The symptom then arrives in somebody else's repository, as a
generator that has never heard of a feature sitting in this `main`.

Changes to docs, tests and samples do not need one.

## Working on the generator

`AgentInfer.Generator` targets **netstandard2.0** and Roslyn **4.14.0**, and
neither is a preference:

- A generator is a plugin loaded into the compiler, and the compiler runs on
  netstandard2.0. Target anything else and the SDK silently declines to load
  the analyzer — which presents as "my generator produced nothing", with no
  error anywhere.
- The Roslyn version is the *oldest* supported compiler, not the newest.
  Raising it raises the minimum SDK for everyone using the library.

**Reading the generated code is the fastest way to understand any of this.** The
samples set `EmitCompilerGeneratedFiles`, so what the generator produced is on
disk after a build:

```
samples/Ledger/obj/generated/AgentInfer.Generator/AgentInfer.Generator.AgentGenerator/
```

### Adding a diagnostic

New rule ids go in `src/AgentInfer.Generator/AnalyzerReleases.Unshipped.md`, and
the build fails without the entry. That is deliberate: a rule id that changes
meaning between versions silently reclassifies somebody else's build. Entries
move to `AnalyzerReleases.Shipped.md` when a version ships, and never change
meaning afterwards.

Prefer a diagnostic to a runtime failure. The argument this library makes is
that the compiler should catch what a framework normally discovers at run time,
so a feature that cannot be diagnosed at compile time is a feature that needs a
second look before it goes in.

## Testing against a real model

The scripted client proves a workflow's *shape*. It has never found a bug.

Every interesting failure in this project's history came from pointing a sample
at a deliberately weak local model — `qwen3:latest` through Ollama — and none
came from a good one. A weak model is not trying to be helpful, so it does
exactly what it was told, and what it was told is where the bugs are. The
"What a real model changed" section of the README is entirely findings from
that.

If you are changing prompt construction, schema emission, or reply binding,
run it live before you open the PR:

```bash
dotnet run --project samples/Incident -- --live --metrics
```

## Working with an agent

`CLAUDE.md` at the root carries the same rules as this file in the form an agent
reads, plus the traps that fail silently. `.claude/skills/` holds the workflows
worth doing the same way every time — `verify` before pushing, `add-diagnostic`
for a new `AIN` rule, `release` for cutting a version.

Keep them true. A stale instruction file is worse than none, because it is
followed.

## Style

Match the surrounding code. The one thing worth stating outright: **comments
here explain why, not what.** A comment that restates the line above it is
noise; a comment naming the failure a line prevents is the reason most of this
code looks the way it does. If you are removing something that looks redundant,
check whether a comment already says why it is not.
