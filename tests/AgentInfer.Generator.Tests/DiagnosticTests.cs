using Microsoft.CodeAnalysis;

using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// The build errors that replace the other framework's runtime surprises.
/// </summary>
/// <remarks>
/// Each case names the failure it prevents. These are the product, so they get
/// tested like it.
/// </remarks>
public sealed class DiagnosticTests
{
    private static string[] Ids(string source) =>
        [.. GeneratorHarness.Run(source).Diagnostics.Select(d => d.Id)];

    /// <summary>NOOA: an f-string is not a docstring, so you silently inherit
    /// the framework's own internal prompt.</summary>
    [Fact]
    public void An_empty_agent_prompt_is_AIN001() =>
        Assert.Contains("AIN001", Ids("""
            using AgentInfer;
            [Agent("")]
            public interface IThing { }
            """));

    /// <summary>NOOA: a method with no docstring gets an empty task prompt and
    /// behaves almost right.</summary>
    [Fact]
    public void A_method_without_a_prompt_is_AIN002() =>
        Assert.Contains("AIN002", Ids("""
            using System.Threading.Tasks;
            using AgentInfer;
            [Agent("You are terse.")]
            public interface IThing { public Task<string> DoAsync(); }
            """));

    [Fact]
    public void A_non_task_return_is_AIN003() =>
        Assert.Contains("AIN003", Ids("""
            using AgentInfer;
            [Agent("You are terse.")]
            public interface IThing { [Prompt("Do it.")] public string Do(); }
            """));

    /// <summary>The one that cost a week: in NOOA an undecorated method silently
    /// executes model-written code. Here asking for CodeAct fails loudly until
    /// the sandbox exists, rather than quietly downgrading to Predict.</summary>
    [Fact]
    public void Asking_for_CodeAct_is_AIN004() =>
        Assert.Contains("AIN004", Ids("""
            using System.Threading.Tasks;
            using AgentInfer;
            [Agent("You are terse.")]
            public interface IThing
            {
                [Prompt("Do it.")]
                [Strategy(Strategies.CodeAct)]
                public Task<string> DoAsync();
            }
            """));

    [Fact]
    public void Predict_is_the_default_and_needs_no_attribute()
    {
        var (output, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using AgentInfer;
            [Agent("You are terse.")]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("CompleteTextAsync", output);
    }
}
