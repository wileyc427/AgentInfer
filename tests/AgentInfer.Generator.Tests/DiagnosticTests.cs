using Microsoft.CodeAnalysis;

using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// The build errors that turn run-time surprises into compile-time ones.
/// </summary>
/// <remarks>
/// Each case names the failure it prevents. These are the product, so they get
/// tested like it.
/// </remarks>
public sealed class DiagnosticTests
{
    private static string[] Ids(string source) =>
        [.. GeneratorHarness.Run(source).Diagnostics.Select(d => d.Id)];

    /// <summary>An agent with no prompt runs on whatever the runtime
    /// supplies.</summary>
    [Fact]
    public void An_empty_agent_prompt_is_AIN001() =>
        Assert.Contains("AIN001", Ids("""
            using AgentInfer;
            [Agent("")]
            public interface IThing { }
            """));

    /// <summary>A method with no prompt gets an empty task and behaves almost
    /// right.</summary>
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

    /// <summary>Asking for code execution fails loudly while no sandbox exists,
    /// rather than quietly downgrading to Predict.</summary>
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
