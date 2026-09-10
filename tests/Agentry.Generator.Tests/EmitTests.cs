using Xunit;

namespace Agentry.Generator.Tests;

/// <summary>
/// What the generator emits for a well-formed agent.
/// </summary>
/// <remarks>
/// These assert on the generated <em>text</em> rather than on a model, which is
/// deliberate. The bug that motivated this file compiled cleanly and produced a
/// class with the right shape — it just routed <c>Task&lt;string&gt;</c> down
/// the JSON path and JSON-encoded every string argument into its own prompt.
/// Nothing but reading the output catches that.
/// </remarks>
public sealed class EmitTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Agentry;

        namespace Demo;

        public sealed record Verdict(bool Approved, int Score);

        [Agent("You are terse.")]
        public interface IAnalyst
        {
            [Prompt("Summarise it.")]
            public Task<string> SummariseAsync(string spending, CancellationToken ct = default);

            [Prompt("Judge it.")]
            public Task<Verdict> ReviewAsync(string summary, CancellationToken ct = default);
        }
        """;

    [Fact]
    public void Implements_the_interface_with_a_conventional_name()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Source);

        Assert.Empty(diagnostics);
        Assert.Contains("public sealed partial class AnalystAgent : IAnalyst", output);
    }

    [Fact]
    public void The_system_prompt_becomes_a_constant()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("private const string SystemPrompt = @\"You are terse.\"", output);
    }

    [Fact]
    public void A_string_return_takes_the_text_path()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("CompleteTextAsync", output);
        // The regression: Task<string> once went through CompleteJsonAsync<string>,
        // because FullyQualifiedFormat renders string as `string` and the check
        // compared display strings instead of SpecialType.
        Assert.DoesNotContain("CompleteJsonAsync<string>", output);
    }

    [Fact]
    public void A_typed_return_takes_the_json_path_fully_qualified()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("CompleteJsonAsync<global::Demo.Verdict>", output);
    }

    [Fact]
    public void String_arguments_are_passed_through_unencoded()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("new(@\"spending\", spending)", output);
        // Same regression, other half: every string was being run through
        // JsonSerializer.Serialize and arriving at the model wrapped in quotes.
        Assert.DoesNotContain("JsonSerializer.Serialize(spending)", output);
    }

    [Fact]
    public void Parameter_types_are_not_hand_prefixed_with_global()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        // `global::string` is not valid C#; it binds to nothing and surfaces as
        // CS0535 against the class rather than against the parameter.
        Assert.DoesNotContain("global::string", output);
    }
}
