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
            [Prompt("Summarize it.")]
            public Task<string> SummarizeAsync(string spending, CancellationToken ct = default);

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

/// <summary>
/// The schema for what the model must produce.
/// </summary>
/// <remarks>
/// The gap this closes made the library's claim half true: tool parameters had
/// a compile-time schema and return types did not, so a model was told "reply
/// with JSON" and left to guess which. A real run answered
/// <c>{"supported": true}</c> to a <c>Verdict(bool, int, string[])</c>.
/// </remarks>
public sealed class ReturnSchemaTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Agentry;

        namespace Demo;

        public sealed record Verdict(bool Approved, int Score, string[] Problems);
        public sealed record Row(string Name, decimal Amount);
        public sealed record Report(Row[] Rows);

        [Agent("You are terse.")]
        public interface IAnalyst
        {
            [Prompt("Judge it.")]
            public Task<Verdict> ReviewAsync(string draft);

            [Prompt("Summarize it.")]
            public Task<string> SummarizeAsync();

            [Prompt("Report.")]
            public Task<Report> ReportAsync();
        }
        """;

    [Fact]
    public void A_record_return_gets_an_object_schema_with_everything_required()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains(
            """{""type"":""object"",""properties"":{""approved"":{""type"":""boolean""},""score"":{""type"":""integer""},""problems"":{""type"":""array"",""items"":{""type"":""string""}}},""required"":[""approved"",""score"",""problems""],""additionalProperties"":false}""",
            output);
    }

    [Fact]
    public void Property_names_are_camelCased_to_match_the_binder()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // A schema that disagrees with JsonSerializerOptions.Web is worse than
        // none: the model obeys it and the bind fails anyway.
        Assert.Contains(@"""approved""", output);
        Assert.DoesNotContain(@"""Approved""", output);
    }

    [Fact]
    public void Nested_records_are_described_rather_than_given_up_on()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains(
            """{""type"":""array"",""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""amount"":{""type"":""number""}}""",
            output);
    }

    [Fact]
    public void A_text_return_gets_no_schema()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // Telling a model to match {"type":"string"} is a way to get a JSON
        // document containing prose.
        var summarize = output[output.IndexOf("SummarizeAsync", StringComparison.Ordinal)..];
        var upToCall = summarize[..summarize.IndexOf("CompleteTextAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("ResponseSchema", upToCall);
    }
}

/// <summary>
/// Value constraints, in the schema the model is given.
/// </summary>
/// <remarks>
/// A schema of <c>{"type":"integer"}</c> is a shape and not a contract. A real
/// run answered <c>score: 100</c> for a field meant to be 1–5 and bound
/// cleanly, because 100 is a perfectly good integer.
/// </remarks>
public sealed class ConstraintTests
{
    private const string Source = """
        using System.ComponentModel.DataAnnotations;
        using System.Threading.Tasks;
        using Agentry;

        namespace Demo;

        public sealed record Verdict(
            [property: Range(1, 5)] int Score,
            [property: MaxLength(200)] string Summary,
            [property: MaxLength(3)] string[] Problems);

        [Agent("You are terse.")]
        public interface IAnalyst
        {
            [Prompt("Judge it.")]
            public Task<Verdict> ReviewAsync();
        }
        """;

    [Fact]
    public void Range_becomes_minimum_and_maximum()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains(
            """"score":{"type":"integer","minimum":1,"maximum":5}"""",
            GeneratorHarness.Unescaped(output));
    }

    [Fact]
    public void MaxLength_on_a_string_is_maxLength()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains(
            """"summary":{"type":"string","maxLength":200}"""",
            GeneratorHarness.Unescaped(output));
    }

    [Fact]
    public void MaxLength_on_a_collection_is_maxItems()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // Same attribute, different keyword. Emitting maxLength for an array
        // would be a schema the model cannot satisfy and a validator that
        // disagrees with it.
        Assert.Contains(""""maxItems":3"""", GeneratorHarness.Unescaped(output));
    }
}

/// <summary>
/// Two methods on one interface, two models.
/// </summary>
/// <remarks>
/// Motivated by a real run: given the same correct one-call tool result,
/// qwen3:latest summarized correctly once and wrongly the next time. Fetching
/// the data was never the hard part, so the method that has to reason wants a
/// different model from the one that classifies.
/// </remarks>
public sealed class ModelRoutingTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using Agentry;

        namespace Demo;

        [Agent("You are terse.")]
        public interface IAnalyst
        {
            [Prompt("Reason about it.")]
            [Model("accurate")]
            public Task<string> SummarizeAsync();

            [Prompt("Classify it.")]
            public Task<string> TriageAsync(string request);
        }
        """;

    [Fact]
    public void A_method_with_a_role_resolves_its_own_runner()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("""_router.For(@"accurate").CompleteTextAsync""", output);
    }

    [Fact]
    public void A_method_without_one_keeps_the_agents_default()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("_runner.CompleteTextAsync", output);
    }

    [Fact]
    public void The_router_is_a_dependency_only_when_something_asks_for_a_role()
    {
        var (routed, _) = GeneratorHarness.Run(Source);
        Assert.Contains("global::Agentry.IModelRouter router", routed);

        var (plain, _) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using Agentry;
            [Agent("You are terse.")]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        // The common case stays one dependency, so a router in a constructor is
        // a signal rather than boilerplate to skim past.
        Assert.DoesNotContain("IModelRouter", plain);
    }

    [Fact]
    public void An_empty_role_is_AGT007()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using Agentry;
            [Agent("You are terse.")]
            public interface IThing
            {
                [Prompt("Do it.")]
                [Model("")]
                public Task<string> DoAsync();
            }
            """);

        // An empty role resolves to nothing and presents as a missing
        // registration, three layers from the attribute that caused it.
        Assert.Contains(diagnostics, d => d.Id == "AGT007");
    }
}
