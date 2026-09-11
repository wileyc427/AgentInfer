using Xunit;

namespace Agentry.Generator.Tests;

/// <summary>
/// Prompts that live in a file rather than in the attribute.
/// </summary>
/// <remarks>
/// The claim being tested is narrow and worth stating: a prompt file changes
/// where the text is authored and nothing else. What the generator emits is the
/// same constant, so nothing is read at run time and the trimming and audit
/// properties an inline prompt has are unchanged.
/// </remarks>
public sealed class PromptFileTests
{
    private const string Agent = """
        using System.Threading.Tasks;
        using Agentry;
        [Agent(PromptFile = "Prompts/thing.md")]
        public interface IThing
        {
            [Prompt("Do it.")]
            public Task<string> DoAsync();
        }
        """;

    private static string[] Ids(
        string source,
        (string Path, string Content)[]? files = null,
        string? projectDirectory = null) =>
        [.. GeneratorHarness.Run(source, files, projectDirectory).Diagnostics.Select(d => d.Id)];

    [Fact]
    public void A_prompt_file_becomes_the_generated_constant()
    {
        var (output, diagnostics) = GeneratorHarness.Run(
            Agent,
            [("/repo/app/Prompts/thing.md", "You are terse.")],
            projectDirectory: "/repo/app/");

        Assert.Empty(diagnostics);
        Assert.Contains("private const string SystemPrompt = @\"You are terse.\";", output);
    }

    /// <summary>
    /// The whole point of the design: nothing about the emitted file records
    /// that the text arrived from disk, except a comment for the reader.
    /// </summary>
    [Fact]
    public void The_generated_file_names_where_the_prompt_came_from()
    {
        var (output, _) = GeneratorHarness.Run(
            Agent,
            [("/repo/app/Prompts/thing.md", "You are terse.")],
            projectDirectory: "/repo/app/");

        Assert.Contains("// Prompt read at compile time from: Prompts/thing.md", output);
    }

    /// <summary>Windows separators in the attribute, forward ones on disk.</summary>
    [Fact]
    public void Separators_do_not_have_to_match()
    {
        var (output, diagnostics) = GeneratorHarness.Run(
            """
            using System.Threading.Tasks;
            using Agentry;
            [Agent(PromptFile = @"Prompts\thing.md")]
            public interface IThing
            {
                [Prompt("Do it.")]
                public Task<string> DoAsync();
            }
            """,
            [("/repo/app/Prompts/thing.md", "You are terse.")],
            projectDirectory: "/repo/app/");

        Assert.Empty(diagnostics);
        Assert.Contains("You are terse.", output);
    }

    /// <summary>
    /// The first thing anyone hits: the file is right there in the project and
    /// the compiler cannot see it, because AdditionalFiles is opt-in.
    /// </summary>
    [Fact]
    public void A_file_outside_AdditionalFiles_is_AGT008() =>
        Assert.Contains("AGT008", Ids(Agent, [], projectDirectory: "/repo/app/"));

    [Fact]
    public void AGT008_carries_the_line_to_paste()
    {
        var diagnostic = GeneratorHarness.Run(Agent, [], "/repo/app/").Diagnostics.Single();

        Assert.Contains(
            "<AdditionalFiles Include=\"Prompts/thing.md\" />",
            diagnostic.GetMessage());
    }

    /// <summary>
    /// Two sources for one string means one of them is stale, and nothing here
    /// can tell which — so neither wins.
    /// </summary>
    [Fact]
    public void A_prompt_and_a_prompt_file_together_is_AGT009() =>
        Assert.Contains("AGT009", Ids(
            """
            using System.Threading.Tasks;
            using Agentry;
            [Agent("You are terse.", PromptFile = "Prompts/thing.md")]
            public interface IThing
            {
                [Prompt("Do it.")]
                public Task<string> DoAsync();
            }
            """,
            [("/repo/app/Prompts/thing.md", "You are verbose.")],
            projectDirectory: "/repo/app/"));

    /// <summary>
    /// Only reachable without a project directory, which is why the generator
    /// asks for one rather than matching by suffix when it can help it.
    /// </summary>
    [Fact]
    public void A_name_matching_two_files_is_AGT010() =>
        Assert.Contains("AGT010", Ids(
            Agent,
            [
                ("/repo/app/v1/Prompts/thing.md", "One."),
                ("/repo/app/v2/Prompts/thing.md", "Two."),
            ]));

    /// <summary>
    /// With the project directory known, the same two files are unambiguous:
    /// neither is at the path the agent named, so this is AGT008 and not a
    /// coin toss between them.
    /// </summary>
    [Fact]
    public void A_project_directory_resolves_what_suffix_matching_cannot() =>
        Assert.Contains("AGT008", Ids(
            Agent,
            [
                ("/repo/app/v1/Prompts/thing.md", "One."),
                ("/repo/app/v2/Prompts/thing.md", "Two."),
            ],
            projectDirectory: "/repo/app/"));

    /// <summary>
    /// A file that exists and says nothing is the failure AGT001 is for. It
    /// arrives by a different road and gets the same answer.
    /// </summary>
    [Fact]
    public void An_empty_prompt_file_is_AGT001() =>
        Assert.Contains("AGT001", Ids(
            Agent,
            [("/repo/app/Prompts/thing.md", "   \n")],
            projectDirectory: "/repo/app/"));

    /// <summary>
    /// An inline prompt must not start reading files, or every existing agent
    /// would pick up whatever AdditionalFiles the project happens to carry.
    /// </summary>
    [Fact]
    public void An_inline_prompt_ignores_additional_files()
    {
        var (output, diagnostics) = GeneratorHarness.Run(
            """
            using System.Threading.Tasks;
            using Agentry;
            [Agent("You are terse.")]
            public interface IThing
            {
                [Prompt("Do it.")]
                public Task<string> DoAsync();
            }
            """,
            [("/repo/app/Prompts/thing.md", "You are verbose.")],
            projectDirectory: "/repo/app/");

        Assert.Empty(diagnostics);
        Assert.Contains("You are terse.", output);
        Assert.DoesNotContain("You are verbose.", output);
    }
}
