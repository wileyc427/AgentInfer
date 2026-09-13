---
name: verify
description: Run the full local verification for AgentInfer before pushing — build both target frameworks with warnings as errors, run the test suite on net8.0 and net10.0, run both scripted samples, and pack and open the nupkg. Use before any push, and always after a change to a .csproj, Directory.Build.props, Directory.Build.targets, Directory.Packages.props or the workflow.
---

# Verify a change

CI runs ubuntu and windows, Debug and Release. This is the same ground locally.
Run it before pushing; a red CI run costs a cycle and reviewer trust.

## 1. Build and test

```bash
dotnet build -c Release -warnaserror
dotnet test  -c Release
```

`dotnet test` must report **both** target frameworks. Seeing only net10.0 means
the net8.0 runtime is missing and half the matrix silently did not run:

```bash
dotnet --list-runtimes | grep NETCore
```

## 2. Run the samples

They are smoke tests, not decoration — between them they exercise the generator,
the tool loop, the permission gate, enum binding and the scope counters.

```bash
dotnet run --project samples/Incident -c Release
dotnet run --project samples/Intake   -c Release
```

Both run against a scripted `IChatClient`, so neither needs an endpoint or a
key. Incident runs a fully generated agent; Intake runs a hand-written one over
generated tools. A change that breaks only the second is exactly what the first
would not notice.

## 3. Pack, and open what you packed

**Do this for any change to a project file or to the build.** A green build is
not evidence the package is correct — the analyzer has been duplicated, the
targets file has vanished, and the XML documentation has been enforced but never
emitted, all past a clean build.

```bash
dotnet pack -c Release -o /tmp/pack
unzip -l /tmp/pack/AgentInfer.0.*.nupkg
```

`AgentInfer` must contain **all** of:

| | |
| --- | --- |
| `lib/net8.0/` and `lib/net10.0/` | both targets, each with a `.dll` and an `.xml` |
| `analyzers/dotnet/cs/AgentInfer.Generator.dll` | exactly once |
| `buildTransitive/AgentInfer.targets` | or `PromptFile` silently does nothing for consumers |
| `README.md` | or the nuget.org listing is blank |

## 4. Version guard

```bash
.github/version-guard.sh origin/main
```

Exit 0 means nothing shippable changed or the version moved. Exit 1 names the
files. Exit 2 means it could not resolve the base — fetch it.

## When something fails

Reproduce before fixing. If CI failed and you cannot reproduce locally, check
the SDK first: a generator is loaded into the compiler and is sensitive to the
patch version in a way ordinary code is not.
