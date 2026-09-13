using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// Tools generated with no agent attached.
/// </summary>
/// <remarks>
/// The split the library's own numbers argue for. An agent implementation is a
/// prompt constant, a constructor and a call site per method — mechanical, and
/// fine to write by hand. The tool half is a schema per tool, a permission list
/// and a dispatch switch that binds each argument from its static type, which
/// is derived data and rots when a signature changes. <c>[AgentTools]</c> takes
/// the second without the first.
/// </remarks>
public sealed class StandaloneToolsTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        public enum Window { Day, Week }

        [AgentTools]
        public sealed class IntakeTools
        {
            [AgentTool("Every ticket waiting in the queue.")]
            [RequiresPermission("intake.read")]
            public string[] Waiting() => [];

            [AgentTool("The body of one ticket.")]
            [RequiresPermission("intake.read")]
            public Task<string> BodyAsync(string id, Window window, CancellationToken ct = default) =>
                Task.FromResult("");

            public string Debug() => "";
        }
        """;

    [Fact]
    public void An_invoker_is_generated_with_no_agent_present()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Source);

        Assert.Empty(diagnostics);
        Assert.Contains("public sealed partial class IntakeToolsInvoker : global::AgentInfer.ToolInvoker", output);

        // Nothing else. The point of the attribute is that the implementation
        // class is the caller's to write.
        Assert.DoesNotContain(": IIntake", output);
    }

    [Fact]
    public void The_manifest_lives_on_the_invoker()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("public static global::AgentInfer.ToolManifest Tools { get; } = new(", output);
        Assert.Contains("public override global::AgentInfer.ToolManifest Manifest => Tools;", output);
    }

    [Fact]
    public void Schemas_and_permissions_are_still_compile_time_constants()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        var text = GeneratorHarness.Unescaped(output);

        // The half that pays: derived from the parameter types, not reflected
        // over at startup.
        Assert.Contains("\"id\":{\"type\":\"string\"}", text);
        Assert.Contains("\"window\":{\"type\":\"string\",\"enum\":[\"day\",\"week\"]}", text);
        Assert.Contains("new string[] { @\"intake.read\" }", output);
    }

    [Fact]
    public void Dispatch_binds_each_argument_from_its_static_type()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("arguments.GetProperty(@\"id\").GetString()!", output);
        Assert.Contains("var result = await _tools.BodyAsync(id, window, ct)", output);
    }

    [Fact]
    public void Results_render_through_an_overload_rather_than_a_serializer()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        // The choice moves to compile time, where the rest of the tool surface
        // already makes it.
        Assert.Contains("return global::AgentInfer.ToolResult.Render(result);", output);
        Assert.DoesNotContain("JsonSerializer.Serialize(result)", output);
    }

    [Fact]
    public void A_result_no_overload_can_render_is_refused_at_build()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using AgentInfer;

            namespace Demo;

            public sealed record Row(string Name, decimal Total);

            [AgentTools]
            public sealed class Tools
            {
                [AgentTool("Everything, in one call.")]
                [RequiresPermission("read")]
                public IReadOnlyList<Row> Overview() => [];
            }
            """);

        // An error rather than a reflective fallback: tools are a menu that
        // grows, and a silent fallback is how the parameter schemas would have
        // rotted if they had not been compile-time from the start.
        var reported = Assert.Single(diagnostics, d => d.Id == "AIN016");
        Assert.Contains("IReadOnlyList", reported.GetMessage());
    }

    [Fact]
    public void A_declared_context_renders_what_no_overload_can()
    {
        var (output, diagnostics) = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            using AgentInfer;

            [assembly: AgentInferJson(typeof(Demo.DemoJson))]

            namespace Demo;

            public sealed record Row(string Name, decimal Total);

            [JsonSourceGenerationOptions(
                PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                UseStringEnumConverter = true,
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true)]
            [JsonSerializable(typeof(IReadOnlyList<Row>))]
            internal partial class DemoJson : JsonSerializerContext;

            [AgentTools]
            public sealed class Tools
            {
                [AgentTool("Everything, in one call.")]
                [RequiresPermission("read")]
                public IReadOnlyList<Row> Overview() => [];
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("DemoJson.Default.GetTypeInfo(typeof(", output);
    }

    [Fact]
    public void A_method_without_the_attribute_is_absent_rather_than_hidden()
    {
        var (output, _) = GeneratorHarness.Run(Source);
        Assert.DoesNotContain("Debug", output);
    }

    [Fact]
    public void The_invoker_name_can_be_overridden()
    {
        var (output, _) = GeneratorHarness.Run("""
            using AgentInfer;

            namespace Demo;

            [AgentTools(InvokerName = "Desk")]
            public sealed class IntakeTools
            {
                [AgentTool("Waiting tickets.")]
                [RequiresPermission("intake.read")]
                public string[] Waiting() => [];
            }
            """);

        Assert.Contains("public sealed partial class Desk : global::AgentInfer.ToolInvoker", output);
    }

    [Fact]
    public void A_tools_type_with_no_tools_is_a_warning()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using AgentInfer;

            namespace Demo;

            [AgentTools]
            public sealed class Empty
            {
                public string Debug() => "";
            }
            """);

        // The generated invoker would offer a model nothing, which presents as
        // an agent that answers without ever calling a tool.
        Assert.Contains(diagnostics, d => d.Id == "AIN012");
    }

    [Fact]
    public void Both_ways_in_describe_one_type_identically()
    {
        var (standalone, _) = GeneratorHarness.Run(Source);

        var (viaAgent, _) = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using AgentInfer;

            namespace Demo;

            public enum Window { Day, Week }

            public sealed class IntakeTools
            {
                [AgentTool("Every ticket waiting in the queue.")]
                [RequiresPermission("intake.read")]
                public string[] Waiting() => [];

                [AgentTool("The body of one ticket.")]
                [RequiresPermission("intake.read")]
                public Task<string> BodyAsync(string id, Window window, CancellationToken ct = default) =>
                    Task.FromResult("");

                public string Debug() => "";
            }

            [Agent("You are terse.", Tools = typeof(IntakeTools))]
            public interface IIntake
            {
                [Prompt("Summarize.")]
                public Task<string> SummarizeAsync();
            }
            """);

        // One reader, one emitter. A second copy of either would start
        // identical and diverge on the first thing one of them learned — and
        // the symptom is a manifest that disagrees with the switch serving it.
        foreach (var schema in new[]
                 {
                     "\"id\":{\"type\":\"string\"}",
                     "\"window\":{\"type\":\"string\",\"enum\":[\"day\",\"week\"]}",
                 })
        {
            Assert.Contains(schema, GeneratorHarness.Unescaped(standalone));
            Assert.Contains(schema, GeneratorHarness.Unescaped(viaAgent));
        }
    }
}
