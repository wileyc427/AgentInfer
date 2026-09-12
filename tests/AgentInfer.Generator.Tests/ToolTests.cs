using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// What <c>[AgentTool]</c> puts in the generated manifest.
/// </summary>
/// <remarks>
/// The schema is asserted as text because it ships as text: a verbatim literal
/// in the emitted file, built at compile time. That is the claim worth
/// protecting — reflecting over the method at startup would work until somebody
/// published trimmed.
/// </remarks>
public sealed class ToolTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        public sealed class Tools
        {
            [AgentTool("Every category.")]
            [RequiresPermission("ledger.read")]
            public string[] Categories() => [];

            [AgentTool("The total for one category.")]
            [RequiresPermission("ledger.read")]
            public decimal TotalFor(string category, int year) => 0m;

            [AgentTool("Move a transaction.")]
            [RequiresPermission("ledger.write")]
            [RequiresPermission("ledger.read")]
            public void Reclassify(string category) { }

            public string NotATool() => "";
        }

        [Agent("You are terse.", Tools = typeof(Tools))]
        public interface IAnalyst
        {
            [Prompt("Do it.")]
            public Task<string> DoAsync();
        }
        """;

    [Fact]
    public void Tools_are_opt_in_one_method_at_a_time()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("@\"Categories\"", output);
        // No attribute, so the generator never saw it. Absent, not hidden —
        // which is the difference from a documentation flag that leaves the
        // method perfectly callable.
        Assert.DoesNotContain("NotATool", output);
    }

    [Fact]
    public void The_schema_is_built_at_compile_time_from_the_parameters()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains(
            """{""type"":""object"",""properties"":{""category"":{""type"":""string""},""year"":{""type"":""integer""}},""required"":[""category"",""year""],""additionalProperties"":false}""",
            output);
    }

    [Fact]
    public void A_tool_with_no_parameters_gets_an_empty_object_schema()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("""{""type"":""object"",""properties"":{},""required"":[],""additionalProperties"":false}""", output);
    }

    [Fact]
    public void Every_permission_is_carried()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("""new string[] { @"ledger.write", @"ledger.read" }""", output);
    }

    [Fact]
    public void An_agent_with_no_tools_still_gets_an_empty_manifest()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using AgentInfer;
            [Agent("You are terse.")]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        // Present and empty rather than absent, so a caller never has to ask
        // whether this agent has a manifest before reading it.
        Assert.Contains("ToolManifest Tools { get; }", output);
    }

    [Fact]
    public void A_parameter_with_no_schema_is_AIN005()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using AgentInfer;

            public sealed class Ledger { }

            public sealed class Tools
            {
                [AgentTool("Takes something unrepresentable.")]
                [RequiresPermission("ledger.read")]
                public void Take(Ledger ledger) { }
            }

            [Agent("You are terse.", Tools = typeof(Tools))]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        Assert.Contains(diagnostics, d => d.Id == "AIN005");
    }

    [Fact]
    public void A_tool_with_no_permission_is_AIN006()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using AgentInfer;

            public sealed class Tools
            {
                [AgentTool("Unguarded.")]
                public void Go() { }
            }

            [Agent("You are terse.", Tools = typeof(Tools))]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        // A warning, not an error: a tool everyone may call is a decision
        // somebody may legitimately make, and it should be one they made.
        Assert.Contains(diagnostics, d => d.Id == "AIN006");
    }

    [Fact]
    public void A_cancellation_token_is_not_part_of_the_schema()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using AgentInfer;

            public sealed class Tools
            {
                [AgentTool("Reads.")]
                [RequiresPermission("read")]
                public Task ReadAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
            }

            [Agent("You are terse.", Tools = typeof(Tools))]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        // Plumbing, not an argument the model supplies.
        Assert.DoesNotContain("\"\"ct\"\"", output);
        Assert.Contains("\"\"key\"\"", output);
    }
}

/// <summary>
/// The generated dispatcher: typed binding, and no reflection anywhere.
/// </summary>
public sealed class InvokerTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        public sealed class Tools
        {
            [AgentTool("Reads.")]
            [RequiresPermission("read")]
            public decimal TotalFor(string category, int year) => 0m;

            [AgentTool("Writes.")]
            [RequiresPermission("write")]
            public Task SaveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        }

        [Agent("You are terse.", Tools = typeof(Tools))]
        public interface IAnalyst
        {
            [Prompt("Do it.")]
            public Task<string> DoAsync();
        }
        """;

    [Fact]
    public void Arguments_are_read_with_accessors_chosen_at_compile_time()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // The static type picked the accessor. Nothing inspects the method at
        // run time, which is what lets the tool surface survive trimming.
        Assert.Contains("""arguments.GetProperty(@"category").GetString()!""", output);
        Assert.Contains("""arguments.GetProperty(@"year").GetInt32()""", output);
    }

    [Fact]
    public void A_cancellation_token_is_passed_but_never_bound_from_json()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("_tools.SaveAsync(key, ct)", output);
        Assert.DoesNotContain("""GetProperty(@"ct")""", output);
    }

    [Fact]
    public void The_invoker_reuses_the_agents_manifest_rather_than_rebuilding_it()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.Contains("public override global::AgentInfer.ToolManifest Manifest => AnalystAgent.Tools;", output);
    }

    [Fact]
    public void An_agent_with_no_tools_gets_no_invoker()
    {
        var (output, _) = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using AgentInfer;
            [Agent("You are terse.")]
            public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
            """);

        // A dispatcher with nothing to dispatch is a class that exists to be
        // confusing.
        Assert.DoesNotContain("ToolInvoker", output);
    }
}

/// <summary>
/// How a generation method is wired once its agent has tools.
/// </summary>
public sealed class ToolRoutingTests
{
    private const string WithTools = """
        using System.Threading;
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        public sealed record Verdict(bool Approved);

        public sealed class Tools
        {
            [AgentTool("Reads.")]
            [RequiresPermission("read")]
            public decimal TotalFor(string category) => 0m;
        }

        [Agent("You are terse.", Tools = typeof(Tools))]
        public interface IAnalyst
        {
            [Prompt("Summarize.")]
            [Strategy(Strategies.Predict, MaxIterations = 9)]
            public Task<string> SummarizeAsync(CancellationToken ct = default);

            [Prompt("Judge.")]
            public Task<Verdict> ReviewAsync(string summary, CancellationToken ct = default);
        }
        """;

    private const string WithoutTools = """
        using System.Threading.Tasks;
        using AgentInfer;
        [Agent("You are terse.")]
        public interface IThing { [Prompt("Do it.")] public Task<string> DoAsync(); }
        """;

    [Fact]
    public void The_constructor_requires_an_invoker_and_an_authorizer()
    {
        var (output, _) = GeneratorHarness.Run(WithTools);

        // Both required rather than optional: an authorizer defaulting to
        // "allow" would make the safe path the one you have to remember.
        Assert.Contains(
            "public AnalystAgent(global::AgentInfer.AgentRunner runner, AnalystAgentTools tools, global::AgentInfer.IToolAuthorizer authorizer)",
            output);
    }

    [Fact]
    public void Both_return_shapes_get_tools_not_just_the_text_one()
    {
        var (output, _) = GeneratorHarness.Run(WithTools);

        // Tying tools to the return type would be a rule nobody would guess.
        Assert.Contains("CompleteWithToolsAsync(call, _tools, _authorizer,", output);
        Assert.Contains("CompleteJsonWithToolsReflectivelyAsync<global::Demo.Verdict>(call, _tools, _authorizer,", output);
    }

    [Fact]
    public void MaxIterations_bounds_the_tool_loop_not_only_CodeAct()
    {
        var (output, _) = GeneratorHarness.Run(WithTools);

        Assert.Contains("_tools, _authorizer, 9,", output);
        // The default applies to the method that did not say.
        Assert.Contains("_tools, _authorizer, 6,", output);
    }

    [Fact]
    public void An_agent_without_tools_keeps_the_plain_constructor_and_the_plain_path()
    {
        var (output, _) = GeneratorHarness.Run(WithoutTools);

        Assert.Contains("public ThingAgent(global::AgentInfer.AgentRunner runner)", output);
        Assert.Contains("CompleteTextAsync(call,", output);
        Assert.DoesNotContain("_authorizer", output);
    }
}
