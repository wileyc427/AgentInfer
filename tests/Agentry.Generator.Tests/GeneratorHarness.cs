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

    /// <summary>
    /// Generated output with verbatim-literal escaping undone.
    /// </summary>
    /// <remarks>
    /// Schemas are emitted inside <c>@"..."</c>, so every quote is doubled and
    /// an assertion written the obvious way silently fails to match. Undoing it
    /// once here means a test can quote the schema as the model will see it,
    /// which is the form worth reading in a failure message.
    /// </remarks>
    public static string Unescaped(string output) => output.Replace("\"\"", "\"");

    /// <summary>
    /// Every assembly the compiled test source might need.
    /// </summary>
    /// <remarks>
    /// Loaded assemblies alone are not enough, and the failure is confusing: an
    /// attribute whose assembly is absent does not error, it resolves to an
    /// error type, so the generator simply does not see it and emits a schema
    /// with the constraint missing. The test then fails on a substring while
    /// the same code works in the sample. Anything matched by metadata name
    /// must be named here.
    /// </remarks>
    private static IEnumerable<MetadataReference> References() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(AgentAttribute).GetTypeInfo().Assembly.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(System.ComponentModel.DataAnnotations.RangeAttribute).GetTypeInfo().Assembly.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute).GetTypeInfo().Assembly.Location))
            .Distinct();
}
