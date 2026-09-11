using Xunit;

namespace Agentry.Generator.Tests;

/// <summary>
/// The generated contract, and the two build errors that keep it honest.
/// </summary>
/// <remarks>
/// Generators do not chain, so this generator cannot see what
/// System.Text.Json's produces. It can see the attributes that drive it —
/// which is enough to catch, at build, every way the two can fail to line up.
/// </remarks>
public sealed class ReplyContractTests
{
    private const string Context = """
        using System.Text.Json.Serialization;
        using System.Threading.Tasks;
        using System.ComponentModel.DataAnnotations;
        using Agentry;

        [assembly: AgentryJson(typeof(Demo.DemoJson))]

        namespace Demo;

        public sealed record Verdict(bool Approved, [property: Range(1, 5)] int Score);

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
    public void A_contract_is_generated_when_the_assembly_declares_a_context()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Context);

        Assert.Empty(diagnostics);
        Assert.Contains("ReviewAsyncContract : global::Agentry.IReplyContract<global::Demo.Verdict>", output);
    }

    [Fact]
    public void The_schema_and_the_check_come_from_one_attribute()
    {
        var (output, _) = GeneratorHarness.Run(Context);
        var text = GeneratorHarness.Unescaped(output);

        // Both from [Range(1, 5)], in the same pass. A model answered score: 100
        // once, and bound cleanly, because these were two facts instead of one.
        Assert.Contains("\"minimum\":1,\"maximum\":5", text);
        Assert.Contains("if (value.Score is < 1 or > 5)", text);
    }

    [Fact]
    public void The_type_is_reached_by_type_rather_than_by_a_guessed_member_name()
    {
        var (output, _) = GeneratorHarness.Run(Context);

        // Default.Verdict happens to be right for a plain record and wrong for
        // a generic or a nested type, and the other generator owns that naming.
        Assert.Contains("Default.GetTypeInfo(typeof(global::Demo.Verdict))", output);
        Assert.DoesNotContain("Default.Verdict", output);
    }

    [Fact]
    public void The_call_no_longer_carries_a_schema_of_its_own()
    {
        var (output, _) = GeneratorHarness.Run(Context);

        // One schema, in the contract. Two would be one of them out of date.
        Assert.DoesNotContain("ResponseSchema =", output);
    }

    [Fact]
    public void Checks_reach_into_a_nested_record()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using System.ComponentModel.DataAnnotations;
            using Agentry;

            [assembly: AgentryJson(typeof(Demo.DemoJson))]

            namespace Demo;

            public sealed record Detail([property: Range(0, 100)] int Confidence);
            public sealed record Verdict(bool Approved, Detail Detail);

            [JsonSerializable(typeof(Verdict))]
            internal partial class DemoJson : JsonSerializerContext;

            [Agent("You are terse.")]
            public interface IAnalyst
            {
                [Prompt("Judge it.")]
                public Task<Verdict> ReviewAsync(string summary);
            }
            """);

        var text = GeneratorHarness.Unescaped(output);

        // The schema walks three levels deep. If the checks stopped at the top
        // the model would be told a bound it was never held to.
        Assert.Contains("\"minimum\":0,\"maximum\":100", text);
        Assert.Contains("if (value.Detail.Confidence is < 0 or > 100)", text);
    }

    [Fact]
    public void A_string_is_measured_in_characters_and_a_list_in_items()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using System.ComponentModel.DataAnnotations;
            using Agentry;

            [assembly: AgentryJson(typeof(Demo.DemoJson))]

            namespace Demo;

            public sealed record Verdict(
                [property: MaxLength(200)] string Summary,
                [property: MinLength(1)] List<string> Problems);

            [JsonSerializable(typeof(Verdict))]
            internal partial class DemoJson : JsonSerializerContext;

            [Agent("You are terse.")]
            public interface IAnalyst
            {
                [Prompt("Judge it.")]
                public Task<Verdict> ReviewAsync(string summary);
            }
            """);

        // The same split the schema makes between maxLength and minItems, so
        // the two agree on what is being measured.
        Assert.Contains("value.Summary.Length > 200", output);
        Assert.Contains("value.Problems.Count < 1", output);
    }

    [Fact]
    public void A_context_that_is_not_one_is_refused()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using Agentry;

            [assembly: AgentryJson(typeof(Demo.NotAContext))]

            namespace Demo;

            public sealed record Verdict(bool Approved);
            public sealed class NotAContext;

            [Agent("You are terse.")]
            public interface IAnalyst
            {
                [Prompt("Judge it.")]
                public Task<Verdict> ReviewAsync(string summary);
            }
            """);

        Assert.Contains(diagnostics, d => d.Id == "AGT013");
    }

    [Fact]
    public void A_return_type_the_context_does_not_serialize_is_named_at_build()
    {
        var (output, diagnostics) = GeneratorHarness.Run("""
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using Agentry;

            [assembly: AgentryJson(typeof(Demo.DemoJson))]

            namespace Demo;

            public sealed record Verdict(bool Approved);
            public sealed record Summary(string Text);

            [JsonSerializable(typeof(Verdict))]
            internal partial class DemoJson : JsonSerializerContext;

            [Agent("You are terse.")]
            public interface IAnalyst
            {
                [Prompt("Judge it.")]
                public Task<Verdict> ReviewAsync(string summary);

                [Prompt("Sum it up.")]
                public Task<Summary> SummariseAsync(string text);
            }
            """);

        // Otherwise this is a null JsonTypeInfo on the first call, or a compile
        // error in a generated file naming a member nobody wrote.
        var reported = Assert.Single(diagnostics, d => d.Id == "AGT014");
        Assert.Contains("Summary", reported.GetMessage());

        // And nothing is emitted against a context that cannot serve it.
        Assert.DoesNotContain("IReplyContract", output);
    }

    [Fact]
    public void Without_the_attribute_the_reflective_path_stays()
    {
        var (output, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using Agentry;

            namespace Demo;

            public sealed record Verdict(bool Approved);

            [Agent("You are terse.")]
            public interface IAnalyst
            {
                [Prompt("Judge it.")]
                public Task<Verdict> ReviewAsync(string summary);
            }
            """);

        // Opt-in, so an assembly that does not care is unaffected — and still
        // says what it is doing rather than pretending to be clean.
        Assert.Empty(diagnostics);
        Assert.Contains("CompleteJsonReflectivelyAsync", output);
        Assert.Contains("#pragma warning disable IL2026, IL3050", output);
    }
}
