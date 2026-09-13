using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// <c>AIN017</c>: a declared context whose options disagree with the reflective
/// path's.
/// </summary>
/// <remarks>
/// The divergence this catches had happened, in five files, and nothing said
/// so: a model returning <c>"score": "4"</c> bound through
/// <c>CompleteJsonReflectivelyAsync</c> and threw through an
/// <c>IReplyContract</c>. The tests below are written against that reply.
/// </remarks>
public sealed class BindingOptionsTests
{
    private static string Source(string options, int agents = 1) => $$"""
        using System.Text.Json.Serialization;
        using System.Threading.Tasks;
        using AgentInfer;

        [assembly: AgentInferJson(typeof(Demo.DemoJson))]

        namespace Demo;

        public sealed record Verdict(bool Approved, int Score);

        {{options}}
        [JsonSerializable(typeof(Verdict))]
        internal partial class DemoJson : JsonSerializerContext;

        {{string.Join("\n\n", Enumerable.Range(1, agents).Select(n => $$"""
        [Agent("You are terse.")]
        public interface IAnalyst{{n}}
        {
            [Prompt("Judge it.")]
            public Task<Verdict> ReviewAsync{{n}}(string summary);
        }
        """))}}
        """;

    private const string Complete = """
        [JsonSourceGenerationOptions(
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            UseStringEnumConverter = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true)]
        """;

    [Fact]
    public void A_context_that_matches_the_reflective_path_says_nothing()
    {
        var (_, diagnostics) = GeneratorHarness.Run(Source(Complete));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void The_message_names_every_option_that_disagrees()
    {
        // The exact shape five files in this repository had.
        var (_, diagnostics) = GeneratorHarness.Run(Source("""
            [JsonSourceGenerationOptions(
                PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                UseStringEnumConverter = true,
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true)]
            """));

        var drift = Assert.Single(diagnostics, d => d.Id == "AIN017");
        var message = drift.GetMessage();

        Assert.Contains("PropertyNameCaseInsensitive = true", message);
        Assert.Contains("NumberHandling = JsonNumberHandling.AllowReadingFromString", message);

        // Only what actually disagrees, so the fix is the message.
        Assert.DoesNotContain("UseStringEnumConverter", message);
        Assert.DoesNotContain("PropertyNamingPolicy", message);
    }

    [Fact]
    public void A_context_with_no_options_at_all_names_all_six()
    {
        var (_, diagnostics) = GeneratorHarness.Run(Source(""));

        var drift = Assert.Single(diagnostics, d => d.Id == "AIN017");

        Assert.Equal(6, drift.GetMessage().Split(" = ").Length - 1);
    }

    /// <remarks>
    /// NumberHandling is a flag set. Writing numbers as strings as well is a
    /// wider tolerance than the reflective path, not a different one, and a
    /// diagnostic that cried wolf about it would get suppressed.
    /// </remarks>
    [Fact]
    public void A_wider_flag_still_agrees()
    {
        var (_, diagnostics) = GeneratorHarness.Run(Source("""
            [JsonSourceGenerationOptions(
                PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString,
                UseStringEnumConverter = true,
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true)]
            """));

        Assert.Empty(diagnostics);
    }

    /// <remarks>
    /// The reason it hangs off the assembly attribute rather than the agent
    /// path: there is one context, so there is one thing to say about it.
    /// </remarks>
    [Fact]
    public void It_is_reported_once_per_assembly_not_once_per_agent()
    {
        var (_, diagnostics) = GeneratorHarness.Run(Source("", agents: 3));

        Assert.Single(diagnostics, d => d.Id == "AIN017");
    }

    /// <remarks>
    /// Two diagnostics for one mistake is worse than one, and the target not
    /// being a context is <c>AIN013</c>'s to report.
    /// </remarks>
    [Fact]
    public void A_target_that_is_not_a_context_is_left_to_AIN013()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using AgentInfer;

            [assembly: AgentInferJson(typeof(Demo.NotAContext))]

            namespace Demo;

            public sealed class NotAContext;

            [Agent("You are terse.")]
            public interface IAnalyst
            {
                [Prompt("Judge it.")]
                public Task<string> ReviewAsync(string summary);
            }
            """);

        Assert.Contains(diagnostics, d => d.Id == "AIN013");
        Assert.DoesNotContain(diagnostics, d => d.Id == "AIN017");
    }
}
