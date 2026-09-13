using System.Reflection;
using System.Text.RegularExpressions;

using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// Every emitted type carries <c>[GeneratedCode]</c>.
/// </summary>
/// <remarks>
/// The <c>&lt;auto-generated/&gt;</c> header is a compiler convention and stops
/// at the file. This attribute is what other tooling reads — a coverage run
/// excluding by attribute, and anyone asking which generator wrote a
/// <c>.g.cs</c> they were handed. A type emitted without it is invisible to
/// both, and nothing else in the build would say so.
/// </remarks>
public sealed class GeneratedCodeAttributeTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        public sealed record Verdict(bool Approved, int Score);

        [AgentTools]
        public sealed class Ledger
        {
            [AgentTool("Every category.")]
            [RequiresPermission("ledger.read")]
            public string[] Categories() => [];
        }

        [Agent("You are terse.", Tools = typeof(Ledger))]
        public interface IAnalyst
        {
            [Prompt("Summarize it.")]
            [Model("accurate")]
            public Task<string> SummarizeAsync(string spending, CancellationToken ct = default);

            [Prompt("Judge it.")]
            public Task<Verdict> ReviewAsync(string summary, CancellationToken ct = default);
        }
        """;

    /// <summary>Every top-level type declaration, and what precedes it.</summary>
    private static readonly Regex Declaration = new(
        @"^(?<attribute>\[global::System\.CodeDom\.Compiler\.GeneratedCodeAttribute\([^\)]*\)\]\r?\n)?"
        + @"(?<declaration>(?:public|internal|file) (?:sealed|static) .*)$",
        RegexOptions.Multiline);

    [Fact]
    public void Every_generated_type_is_marked()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Source);

        Assert.Empty(diagnostics);

        var declarations = Declaration.Matches(output);

        // The agent, its tool invoker, the reply contract and the roles class.
        Assert.Equal(4, declarations.Count);

        var unmarked = declarations
            .Where(match => !match.Groups["attribute"].Success)
            .Select(match => match.Groups["declaration"].Value.Trim())
            .ToArray();

        Assert.Empty(unmarked);
    }

    /// <remarks>
    /// Read from the assembly rather than written here. A literal in a test is
    /// a second copy of <c>&lt;Version&gt;</c>, and it would fail on the next
    /// bump for a reason that has nothing to do with what this asserts.
    /// </remarks>
    [Fact]
    public void The_version_is_the_generator_that_wrote_it()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        var version = typeof(AgentGenerator).GetTypeInfo().Assembly.GetName().Version?.ToString();

        Assert.NotNull(version);
        Assert.Contains($"""GeneratedCodeAttribute("AgentInfer.Generator", "{version}")""", output);
    }
}
