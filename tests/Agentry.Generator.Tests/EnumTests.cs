using Xunit;

namespace Agentry.Generator.Tests;

/// <summary>
/// Enums as a contract, which is what makes routing work.
/// </summary>
/// <remarks>
/// Routing is the pattern an OO agent library should be best at: a method
/// returns a closed set, and the caller writes a <c>switch</c> the compiler
/// knows about. It did not work. An enum was emitted as <c>{"type":"string"}</c>
/// with no member list, so the model was told to reply with a string and never
/// told which strings were legal — and the binder had no string-enum converter,
/// so even the right word failed. Both halves are asserted here.
/// </remarks>
public sealed class EnumTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Agentry;

        namespace Demo;

        public enum Urgency { Low, Normal, NeedsAttention }

        public sealed record Triage(Urgency Urgency, string Reason);

        [Agent("You are terse.")]
        public interface ITriage
        {
            [Prompt("How urgent?")]
            public Task<Urgency> ClassifyAsync(string request, CancellationToken ct = default);

            [Prompt("Triage it.")]
            public Task<Triage> ExplainAsync(string request, CancellationToken ct = default);
        }
        """;

    [Fact]
    public void An_enum_return_lists_its_members()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Source);

        Assert.Empty(diagnostics);
        Assert.Contains(
            "{\"type\":\"string\",\"enum\":[\"low\",\"normal\",\"needsAttention\"]}",
            GeneratorHarness.Unescaped(output));
    }

    [Fact]
    public void An_enum_property_lists_its_members_too()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        var text = GeneratorHarness.Unescaped(output);

        // The nested case is the one that would have rotted quietly: a Triage
        // record binds, and only the enum slot is wrong.
        Assert.Contains(
            "\"urgency\":{\"type\":\"string\",\"enum\":[\"low\",\"normal\",\"needsAttention\"]}",
            text);
    }

    [Fact]
    public void Members_are_camel_cased_to_match_the_binder()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // The converter the runtime binds with is camelCase. A schema offering
        // "NeedsAttention" would be obeyed and then rejected, which is the one
        // failure worse than having no schema at all.
        Assert.DoesNotContain("\"NeedsAttention\"", GeneratorHarness.Unescaped(output));
    }

    [Fact]
    public void A_renamed_member_is_offered_under_its_wire_name()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using System.Text.Json.Serialization;
            using Agentry;

            namespace Demo;

            public enum Status
            {
                [JsonStringEnumMemberName("in_progress")] InProgress,
                Done,
            }

            [Agent("You are terse.")]
            public interface IStatus
            {
                [Prompt("What state?")]
                public Task<Status> StateAsync(string request, CancellationToken ct = default);
            }
            """);

        // The converter honours this attribute, so the schema has to as well.
        Assert.Contains("\"enum\":[\"in_progress\",\"done\"]", GeneratorHarness.Unescaped(output));
    }

    [Fact]
    public void A_nullable_enum_is_unwrapped_rather_than_described_as_a_struct()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using Agentry;

            namespace Demo;

            public enum Urgency { Low, High }

            public sealed record Maybe(Urgency? Urgency, string Reason);

            [Agent("You are terse.")]
            public interface IMaybe
            {
                [Prompt("How urgent, if you can tell?")]
                public Task<Maybe> ClassifyAsync(string request, CancellationToken ct = default);
            }
            """);

        var text = GeneratorHarness.Unescaped(output);

        // Nullable<T> reached the object walk as an ordinary struct and was
        // described by its members, so the model was asked for
        // {"hasValue":…,"value":…} — a shape nothing wants.
        Assert.DoesNotContain("hasValue", text);
        Assert.Contains("\"urgency\":{\"type\":\"string\",\"enum\":[\"low\",\"high\"]}", text);
    }

    [Fact]
    public void An_enum_tool_parameter_lists_its_members()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using Agentry;

            namespace Demo;

            public enum Window { Day, Week }

            public sealed class Tools
            {
                [AgentTool("Totals over a window.")]
                [RequiresPermission("ledger.read")]
                public decimal Total(Window window) => 0m;
            }

            [Agent("You are terse.", Tools = typeof(Tools))]
            public interface IAnalyst
            {
                [Prompt("Summarise.")]
                public Task<string> SummariseAsync();
            }
            """);

        var text = GeneratorHarness.Unescaped(output);

        Assert.Contains("\"window\":{\"type\":\"string\",\"enum\":[\"day\",\"week\"]}", text);
        // Read by a generated switch, not Enum.Parse: reflection on the path
        // this library claims is free of it, and blind to a renamed member.
        Assert.Contains("GetString() switch", text);
        Assert.DoesNotContain("System.Enum.Parse", text);
    }

    [Fact]
    public void An_array_tool_parameter_says_what_is_in_it()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using Agentry;

            namespace Demo;

            public sealed class Tools
            {
                [AgentTool("Totals for several categories.")]
                [RequiresPermission("ledger.read")]
                public decimal Total(string[] categories) => 0m;
            }

            [Agent("You are terse.", Tools = typeof(Tools))]
            public interface IAnalyst
            {
                [Prompt("Summarise.")]
                public Task<string> SummariseAsync();
            }
            """);

        // {"type":"array"} alone tells a model nothing about what to put in it.
        Assert.Contains(
            "\"categories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}",
            GeneratorHarness.Unescaped(output));
    }

    [Fact]
    public void A_flags_enum_is_refused_at_build()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System;
            using System.Threading.Tasks;
            using Agentry;

            namespace Demo;

            [Flags]
            public enum Access { Read = 1, Write = 2 }

            [Agent("You are terse.")]
            public interface IAccess
            {
                [Prompt("What access is needed?")]
                public Task<Access> NeededAsync(string request);
            }
            """);

        // A set offered as a choice of one invites "Read, Write", which reads
        // correctly and binds to nothing.
        Assert.Contains(diagnostics, d => d.Id == "AGT011");
    }
}
