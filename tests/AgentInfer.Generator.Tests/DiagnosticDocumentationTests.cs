using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// Every diagnostic is documented, and the documentation says what the
/// descriptor says.
/// </summary>
/// <remarks>
/// <c>docs/authoring.md</c> is where a <c>helpLinkUri</c> points and where
/// anyone pasting an id from a build log lands, so a rule with no section is a
/// dead end at the moment somebody needs it. Checked here rather than read,
/// because a documentation list is exactly the kind of hand-maintained copy
/// that stops matching and says nothing when it does.
/// </remarks>
public sealed class DiagnosticDocumentationTests
{
    private static readonly (string Id, string Title, string Severity)[] Descriptors =
    [
        .. typeof(AgentGenerator).Assembly
            .GetType("AgentInfer.Generator.Diagnostics")!
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(field => field.GetValue(null))
            .OfType<DiagnosticDescriptor>()
            .Select(d => (d.Id, d.Title.ToString(), d.DefaultSeverity.ToString()))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
    ];

    /// <remarks>
    /// Copied beside the assembly by the project file, not located at run time.
    /// Walking up from <c>[CallerFilePath]</c> was the first attempt and cannot
    /// work: <c>ContinuousIntegrationBuild</c> is set on CI, which turns on
    /// deterministic source paths, so the compiler bakes in
    /// <c>/_/tests/...</c> — a path with no repository above it and no bearing
    /// on where anything actually is.
    /// </remarks>
    private static string Authoring => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "docs", "authoring.md"));

    [Fact]
    public void There_is_something_to_check()
    {
        Assert.NotEmpty(Descriptors);
        Assert.Equal(Descriptors.Length, Descriptors.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void Every_diagnostic_has_a_section_stating_its_title_and_severity()
    {
        var undocumented = Descriptors
            .Where(d => !Authoring.Contains(
                $"### {d.Id}\n\n**{d.Title}** · {d.Severity}\n", StringComparison.Ordinal))
            .Select(d => $"{d.Id} ({d.Title}, {d.Severity})")
            .ToArray();

        Assert.Empty(undocumented);
    }

    [Fact]
    public void Every_diagnostic_has_an_index_row()
    {
        var unlisted = Descriptors
            .Where(d => !Authoring.Contains(
                $"| [`{d.Id}`](#{d.Id.ToLowerInvariant()}) | {d.Title} | {d.Severity} |",
                StringComparison.Ordinal))
            .Select(d => d.Id)
            .ToArray();

        Assert.Empty(unlisted);
    }

    /// <remarks>
    /// The other direction. A section for a rule that no longer exists sends a
    /// reader looking for behaviour nothing implements.
    /// </remarks>
    [Fact]
    public void No_section_describes_a_diagnostic_that_does_not_exist()
    {
        var ids = Descriptors.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        var stale = Regex.Matches(Authoring, @"^### (AIN\d+)$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Where(id => !ids.Contains(id))
            .ToArray();

        Assert.Empty(stale);
    }
}
