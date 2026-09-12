## What this changes

<!-- The behavior that is different afterwards, in a sentence or two. -->

## Why

<!-- The failure it prevents, or the thing that was not possible before.
     If a model did something that prompted this, quote what it did — those
     are the most useful lines in this repository's history. -->

## Checks

- [ ] `dotnet build` and `dotnet test` pass locally
- [ ] `<Version>` in `Directory.Build.props` is raised, **or** nothing under `src/` changed
- [ ] A new generator diagnostic has an entry in `AnalyzerReleases.Unshipped.md`, or none was added
- [ ] Package versions, if any changed, moved in `Directory.Packages.props`
- [ ] Run live against a real model if this touches prompt construction, schema emission or reply binding
