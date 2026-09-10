using System.Collections.Immutable;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Agentry.Generator.Tests;

/// <summary>Runs the generator over a string of C# and hands back what it did.</summary>
/// <remarks>
/// Driving <see cref="CSharpGeneratorDriver"/> directly rather than through the
/// analyzer-testing package: the thing worth asserting on is the generated text
/// and the diagnostics, and a driver gives both with no ceremony.
/// </remarks>
internal static class GeneratorHarness
{
    public static (string Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "Test",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver
            .Create(new AgentGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var result = driver.GetRunResult().Results.Single();

        return (
            string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString())),
            result.Diagnostics);
    }

    private static IEnumerable<MetadataReference> References() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(AgentAttribute).GetTypeInfo().Assembly.Location));
}
