using Xunit;

namespace Agentry.Generator.Tests;

/// <summary>
/// Two ways to declare a bound, read by one reader.
/// </summary>
/// <remarks>
/// DataAnnotations is what a .NET developer reaches for, and it is trim-hostile
/// where it is <em>written</em>: <c>MaxLengthAttribute</c>'s constructor carries
/// [RequiresUnreferencedCode], because ValidationAttribute.IsValid inspects
/// arbitrary types. So a record with [Range(1, 5)] on it makes its assembly
/// unverifiable even when nothing reflects over it. [Bounded] and [Sized] are
/// the same facts with no behaviour attached.
/// </remarks>
public sealed class BoundsTests
{
    // A context is declared, so contracts are emitted and the checks can be
    // asserted alongside the schema they have to agree with.
    private static string Agent(string record) => $$"""
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json.Serialization;
        using Agentry;

        [assembly: AgentryJson(typeof(Demo.DemoJson))]

        namespace Demo;

        {{record}}

        [JsonSerializable(typeof(Verdict))]
        internal partial class DemoJson : JsonSerializerContext;

        [Agent("You are terse.")]
        public interface IAnalyst
        {
            [Prompt("Judge it.")]
            public Task<Verdict> ReviewAsync(string summary);
        }
        """;

    [Fact]
    public void Bounded_produces_the_same_schema_and_check_as_Range()
    {
        var (agentry, _) = GeneratorHarness.Run(Agent(
            "public sealed record Verdict([property: Bounded(1, 5)] int Score);"));

        var (annotations, _) = GeneratorHarness.Run(Agent(
            "public sealed record Verdict([property: Range(1, 5)] int Score);"));

        // One reader behind both, so there is no second place to disagree about
        // what the bound means — and the check comes out of the same read.
        foreach (var output in new[] { agentry, annotations })
        {
            var text = GeneratorHarness.Unescaped(output);

            Assert.Contains("\"minimum\":1,\"maximum\":5", text);
            Assert.Contains("if (value.Score is < 1 or > 5)", text);
        }
    }

    [Fact]
    public void Sized_bounds_a_string_in_characters_and_a_list_in_items()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Agent(
            """
            public sealed record Verdict(
                [property: Sized(Max = 400)] string Summary,
                [property: Sized(Min = 1)] List<string> Problems);
            """));

        Assert.Empty(diagnostics);

        var text = GeneratorHarness.Unescaped(output);
        Assert.Contains("\"maxLength\":400", text);
        Assert.Contains("\"minItems\":1", text);
    }

    [Fact]
    public void A_bound_of_one_is_not_described_in_the_plural()
    {
        var (output, _) = GeneratorHarness.Run(Agent(
            "public sealed record Verdict([property: Sized(Min = 1)] List<string> Problems);"));

        // A model reads this sentence. "at least 1 items" is the kind of phrase
        // that makes a reader trust the rest of the message less.
        Assert.Contains("must have at least 1 item\"", output);
        Assert.DoesNotContain("1 items", output);
    }

    [Fact]
    public void An_unset_end_emits_nothing_rather_than_a_vacuous_bound()
    {
        var (output, _) = GeneratorHarness.Run(Agent(
            "public sealed record Verdict([property: Sized(Max = 400)] string Summary);"));

        var text = GeneratorHarness.Unescaped(output);

        // Min defaults to 0, which is every string. A "minLength":0 in the
        // schema and an `is < 0` in the check would both be noise.
        Assert.Contains("\"maxLength\":400", text);
        Assert.DoesNotContain("\"minLength\"", text);
    }

    [Fact]
    public void Bounding_a_property_twice_is_refused()
    {
        var (_, diagnostics) = GeneratorHarness.Run(Agent(
            "public sealed record Verdict([property: Bounded(1, 5)] [property: Range(1, 10)] int Score);"));

        // Two values under one schema keyword is not a precedence question.
        Assert.Contains(diagnostics, d => d.Id == "AGT015");
    }

    [Fact]
    public void A_double_bound_keeps_its_decimal_form()
    {
        var (output, _) = GeneratorHarness.Run(Agent(
            "public sealed record Verdict([property: Bounded(0.0, 1.0)] double Confidence);"));

        var text = GeneratorHarness.Unescaped(output);

        // Two constructors rather than one taking double, because the bound is
        // emitted into a C# pattern as well: `is < 1.0` does not compile
        // against an int.
        Assert.Contains("\"minimum\":0,\"maximum\":1", text);
        Assert.Contains("value.Confidence is < 0 or > 1", text);
    }
}
