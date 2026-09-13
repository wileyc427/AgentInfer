using Xunit;

namespace AgentInfer.Generator.Tests;

/// <summary>
/// <c>IAsyncEnumerable&lt;string&gt;</c> as a return type.
/// </summary>
public sealed class StreamingTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using AgentInfer;

        namespace Demo;

        [AgentTools]
        public sealed class Desk
        {
            [AgentTool("Every open ticket.")]
            [RequiresPermission("desk.read")]
            public string[] Open() => [];
        }

        [Agent("You are terse.", Tools = typeof(Desk))]
        public interface IWriter
        {
            [Prompt("Draft it.")]
            public IAsyncEnumerable<string> DraftAsync(string topic, CancellationToken ct = default);

            [Prompt("Summarize it.")]
            public Task<string> SummarizeAsync(string topic, CancellationToken ct = default);
        }
        """;

    [Fact]
    public void A_streaming_method_returns_the_runner_enumerable_directly()
    {
        var (output, diagnostics) = GeneratorHarness.Run(Source);

        Assert.Empty(diagnostics);
        Assert.Contains(
            "public global::System.Collections.Generic.IAsyncEnumerable<string> DraftAsync(", output);

        // Not an iterator: no async, no [EnumeratorCancellation], nothing to dispose here.
        Assert.DoesNotContain("async global::System.Collections.Generic.IAsyncEnumerable", output);
        Assert.DoesNotContain("EnumeratorCancellation", output);
    }

    [Fact]
    public void It_reaches_the_tool_loop_when_the_agent_has_tools()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        Assert.Contains("StreamWithToolsAsync(call, _tools, _authorizer,", output);
    }

    /// <remarks>
    /// A streaming method has no schema to send and no value to check, so none
    /// of the Json machinery may attach itself to one.
    /// </remarks>
    [Fact]
    public void A_streaming_method_gets_no_schema_and_no_contract()
    {
        var (output, _) = GeneratorHarness.Run(Source);

        var draft = output[output.IndexOf("DraftAsync(", System.StringComparison.Ordinal)..];
        var body = draft[..draft.IndexOf("SummarizeAsync", System.StringComparison.Ordinal)];

        Assert.DoesNotContain("ResponseSchema", body);
        Assert.DoesNotContain("Contract.Instance", body);
    }

    [Fact]
    public void A_non_string_stream_is_still_AIN003()
    {
        var (_, diagnostics) = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using AgentInfer;

            namespace Demo;

            public sealed record Verdict(bool Approved);

            [Agent("You are terse.")]
            public interface IWriter
            {
                [Prompt("Judge it.")]
                public IAsyncEnumerable<Verdict> ReviewAsync(string summary);
            }
            """);

        Assert.Contains(diagnostics, d => d.Id == "AIN003");
    }
}
