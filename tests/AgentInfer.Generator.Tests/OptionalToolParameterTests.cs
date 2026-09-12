using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// A tool argument the model may leave out.
/// </summary>
/// <remarks>
/// <para>
/// Every parameter used to be <c>required</c> and every one was read with
/// <c>GetProperty</c>, which throws on absence. So a filter — "search the bags,
/// optionally above an item level" — had to be spelled as a sentinel the model
/// was told about in prose: pass an empty string for no filter, pass 0 for no
/// floor. Prose is the weakest place to put a rule, and the failure was silent:
/// a model that sent nothing got a <c>KeyNotFoundException</c> out of the
/// dispatch switch.
/// </para>
/// <para>
/// The rule is the C# one and nothing else: a default in the signature makes
/// the argument optional. <c>int?</c> without a default stays required, because
/// that is what it means in the language, and a schema disagreeing with the
/// signature beside it is the worse of the two to trust.
/// </para>
/// </remarks>
public sealed class OptionalToolParameterTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        public enum Sort { Newest, Oldest }

        public sealed class Tools
        {
            [AgentTool("Search the bags.")]
            [RequiresPermission("bags.read")]
            public string Search(
                string character,
                string? query = null,
                int? minItemLevel = null,
                bool includeBank = true,
                Sort order = Sort.Newest) => "";

            [AgentTool("Everything, with no way to ask for less.")]
            [RequiresPermission("bags.read")]
            public string All(string character, int? page) => "";
        }

        [Agent("You are terse.", Tools = typeof(Tools))]
        public interface IBags
        {
            [Prompt("Do it.")]
            public Task<string> DoAsync();
        }
        """;

    [Fact]
    public void A_parameter_with_a_default_is_not_required()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // Every parameter is still described — optional is about `required`,
        // not about whether the model is told the argument exists. So the
        // whole schema, rather than the one keyword: an assertion on `required`
        // alone would pass just as happily if `query` had been dropped.
        Assert.Contains(
            """
            {""type"":""object"",""properties"":{""character"":{""type"":""string""},""query"":{""type"":""string""},""minItemLevel"":{""type"":""integer""},""includeBank"":{""type"":""boolean""},""order"":{""type"":""string"",""enum"":[""newest"",""oldest""]}},""required"":[""character""],""additionalProperties"":false}
            """,
            output,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_nullable_parameter_without_a_default_stays_required()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // `int? page` is a required parameter in C#. The schema says the same.
        Assert.Contains(
            """
            {""type"":""object"",""properties"":{""character"":{""type"":""string""},""page"":{""type"":""integer""}},""required"":[""character"",""page""],""additionalProperties"":false}
            """,
            output,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_omitted_argument_falls_back_to_the_signature_default()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("arguments.TryGetProperty(@\"query\", out var __query)", output, System.StringComparison.Ordinal);

        // The default in the signature, rendered as the C# that reproduces it —
        // so what the signature promises and what an omitted argument does are
        // the same thing by construction rather than by agreement.
        Assert.Contains(": default(string)", output, System.StringComparison.Ordinal);
        Assert.Contains(": default(int?)", output, System.StringComparison.Ordinal);
        Assert.Contains(": true", output, System.StringComparison.Ordinal);

        // An enum default arrives as its underlying integer, so the cast is
        // load-bearing: without it the emitted code assigns an int to an enum
        // and the generated file does not compile.
        Assert.Contains(": (global::Demo.Sort)(0)", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_json_null_is_treated_as_omitted()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // A model told an argument is optional answers with `"query": null`
        // about as readily as by leaving the key out. GetString() on a JSON
        // null throws, so both spellings have to take the default.
        Assert.Contains(
            "__query.ValueKind != global::System.Text.Json.JsonValueKind.Null",
            output,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_parameter_still_binds_directly()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // No TryGetProperty dance where there is nothing to fall back to: an
        // absent required argument should fail, and say which one.
        Assert.Contains("arguments.GetProperty(@\"character\").GetString()!", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_nullable_value_type_gets_a_reader_rather_than_broken_dispatch()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Source);

        // `int?` passed the schema check and returned no reader, so the
        // transform's null-forgiving ReaderFor(...)! emitted a dispatch line
        // ending in a dot. A manifest that compiled and a switch that did not.
        Assert.Contains("(int?)(__minItemLevel.GetInt32())", output, System.StringComparison.Ordinal);
        Assert.Contains("arguments.GetProperty(@\"page\").GetInt32()", output, System.StringComparison.Ordinal);

        Assert.Empty(diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }
}
