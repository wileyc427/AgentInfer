using System.Collections.Immutable;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Agentry.Generator.Tests;

/// <summary>Runs the generator over a string of C# and hands back what it did.</summary>
/// <remarks>
/// Driving <see cref="CSharpGeneratorDriver"/> directly rather than through the
/// analyzer-testing package: the thing worth asserting on is the generated text
/// and the diagnostics, and a driver gives both with no ceremony.
/// </remarks>
internal static class GeneratorHarness
{
    /// <param name="additionalFiles">
    /// What the project put in <c>AdditionalFiles</c>, as (path, content). Paths
    /// are absolute in a real build, so the tests write them that way.
    /// </param>
    /// <param name="projectDirectory">
    /// What MSBuild would report as <c>ProjectDir</c>. Null leaves it unset,
    /// which is the case the generator's suffix fallback exists for.
    /// </param>
    public static (string Output, ImmutableArray<Diagnostic> Diagnostics) Run(
        string source,
        (string Path, string Content)[]? additionalFiles = null,
        string? projectDirectory = null)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "Test",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var texts = (additionalFiles ?? [])
            .Select(file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Content));

        var driver = CSharpGeneratorDriver
            .Create(
                generators: [new AgentGenerator().AsSourceGenerator()],
                additionalTexts: texts,
                parseOptions: null,
                optionsProvider: new StubOptionsProvider(projectDirectory))
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var result = driver.GetRunResult().Results.Single();

        return (
            string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString())),
            result.Diagnostics);
    }

    /// <summary>An <c>AdditionalFiles</c> entry that never touches disk.</summary>
    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(content);
    }

    /// <summary>
    /// Supplies <c>build_property.projectdir</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// The real one comes from the generated analyzer config the SDK writes.
    /// Stubbing it is what lets a test pin the difference between an exact
    /// project-relative match and the suffix fallback, which is where the
    /// ambiguity AGT009 reports comes from.
    /// </remarks>
    private sealed class StubOptionsProvider(string? projectDirectory) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new StubOptions(projectDirectory);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class StubOptions(string? projectDirectory) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (projectDirectory is not null &&
                    key.Equals("build_property.projectdir", StringComparison.OrdinalIgnoreCase))
                {
                    value = projectDirectory;
                    return true;
                }

                value = null!;
                return false;
            }
        }
    }

    private static IEnumerable<MetadataReference> References() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(AgentAttribute).GetTypeInfo().Assembly.Location));
}
