# Gotchas

**Analyzers do not flow transitively through `ProjectReference`.** `AgentInfer`
references the generator as an `Analyzer`, but a project referencing `AgentInfer`
gets the runtime and no generator. It works through a NuGet package; inside
this repo every consuming project references the generator again explicitly.
See `samples/Ledger/Ledger.csproj`.

**`Directory.Build.props` cannot see `$(TargetFramework)`.** It is imported
before the project body, so any condition on the target framework silently
evaluates against an empty string. `IsAotCompatible`, `EnableTrimAnalyzer` and
`EnableSingleFileAnalyzer` sat in that file, conditioned that way, and were
never once set — so nothing in this repo verified the claim that nothing is
discovered at run time. Switching them on found fourteen violations in
`GatedFunction.cs`, the file this README holds up as the reflection-free path.
They live in `Directory.Build.targets` now, which is imported after the project
body. The failure leaves no trace anywhere: no error, no warning, just a
property that is quietly empty.

**The generator must target netstandard2.0**, because it is loaded into the
compiler. Anything else produces an analyzer the SDK declines to load, with no
error anywhere — it presents as "my generator produced nothing".

**Never compare rendered type names.** `SymbolDisplayFormat.FullyQualifiedFormat`
keeps the `string` keyword alias rather than expanding it to `System.String`,
so a display-string comparison silently fails. Use `SpecialType`. The first
draft did it the wrong way, compiled cleanly, and JSON-encoded every string
argument into its own prompt.

**An XML comment cannot contain two consecutive hyphens.** Writing a command
line flag inside a comment in any `.props`, `.targets`, `.csproj` or workflow
XML makes the file unparseable, and MSBuild reports it as every package in the
file losing its version — `NU1015`, naming projects that were never touched.
Name the flag in prose instead. This has now cost time twice.
