using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentInfer;

/// <summary>
/// How many tool calls one generation method actually made.
/// </summary>
/// <remarks>
/// <para>
/// This exists to answer one question with a number instead of an argument:
/// <b>does this workload need the model to write code, or is calling tools one
/// at a time fine?</b>
/// </para>
/// <para>
/// Letting a model compose over tools in generated code buys exactly one thing
/// — fewer round trips — at the cost of executing code it wrote. That trade is
/// obviously worth it at fifteen calls a turn and obviously not at two, and the
/// only way to know which you have is to count. So: count first, decide after.
/// </para>
/// <para>
/// One instance per generation method call, not per agent. "Calls per turn" is
/// the distribution that matters, and a counter shared across turns cannot
/// produce it.
/// </para>
/// </remarks>
public sealed class ToolCallLog
{
    private readonly Dictionary<string, int> _calls = [];

    /// <summary>How many tools this caller was offered.</summary>
    public int Offered { get; init; }

    /// <summary>How many tools exist, before permissions narrowed them.</summary>
    public int Total { get; init; }

    /// <summary>Every tool invocation, successful or denied.</summary>
    public int Invocations { get; private set; }

    /// <summary>Invocations refused because the caller lacked a permission.</summary>
    public int Denials { get; private set; }

    /// <summary>Per-tool counts, so a hot one is visible rather than averaged away.</summary>
    public IReadOnlyDictionary<string, int> ByTool => _calls;

    internal void Record(string tool, bool denied)
    {
        Invocations++;
        if (denied) Denials++;

        _calls[tool] = _calls.TryGetValue(tool, out var count) ? count + 1 : 1;
    }

    /// <summary>`Categories×1, TotalFor×4` — for the one-line summary.</summary>
    public override string ToString() =>
        _calls.Count == 0
            ? "no tool calls"
            : string.Join(", ", _calls.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}×{pair.Value}"));
}

/// <summary>
/// Meters, so the counts are consumable rather than only readable.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Metrics"/> rather than a bespoke callback:
/// it is what OpenTelemetry already collects in .NET, so this shows up in
/// whatever the host already runs without anybody wiring an adapter.
/// <para>
/// <see cref="CallsPerTurn"/> is the histogram this whole file exists for. Its
/// p95 is what says whether a workload would benefit from letting a model
    /// compose tool calls in code rather than one round trip at a time.
/// </para>
/// </remarks>
public static class AgentMetrics
{
    /// <summary>The meter name to enable in an OpenTelemetry pipeline.</summary>
    public const string MeterName = "AgentInfer";

    internal static Meter Meter { get; } = new(MeterName);

    /// <summary>Every tool invocation, tagged by tool and outcome.</summary>
    internal static Counter<long> ToolCalls { get; } =
        Meter.CreateCounter<long>("agentinfer.tool.calls", "{call}", "Tool invocations.");

    /// <summary>Tool calls in one generation method call.</summary>
    internal static Histogram<int> CallsPerTurn { get; } =
        Meter.CreateHistogram<int>("agentinfer.tool.calls_per_turn", "{call}", "Tool calls per generation method call.");

    /// <summary>Tools offered after permissions narrowed the menu.</summary>
    internal static Histogram<int> ToolsOffered { get; } =
        Meter.CreateHistogram<int>("agentinfer.tools.offered", "{tool}", "Tools offered to the model.");

    /// <summary>
    /// Prompt tokens in one generation method call, summed over its requests.
    /// </summary>
    /// <remarks>
    /// Tagged by operation, because the actionable form of "this is expensive"
    /// is which method. A single scalar for the process says the bill went up
    /// and leaves the reader to guess where.
    /// </remarks>
    internal static Histogram<long> InputTokens { get; } =
        Meter.CreateHistogram<long>("agentinfer.tokens.input", "{token}", "Input tokens per generation method call.");

    /// <summary>Generated tokens in one generation method call.</summary>
    internal static Histogram<long> OutputTokens { get; } =
        Meter.CreateHistogram<long>("agentinfer.tokens.output", "{token}", "Output tokens per generation method call.");

    /// <summary>
    /// Output tokens a reasoning model spent thinking rather than answering.
    /// </summary>
    /// <remarks>
    /// Separate because it is the one that explains a latency nobody can
    /// account for from the reply. A model returning a hundred characters after
    /// forty seconds has not been slow; it has been writing somewhere the reply
    /// does not show.
    /// </remarks>
    internal static Histogram<long> ReasoningTokens { get; } =
        Meter.CreateHistogram<long>("agentinfer.tokens.reasoning", "{token}", "Reasoning tokens per generation method call.");

    /// <summary>Prompt tokens served from a provider's cache.</summary>
    /// <remarks>
    /// Counted apart from <see cref="InputTokens"/>, which includes them:
    /// everywhere caching exists these are billed at a fraction, so a cost
    /// estimate that does not subtract them is wrong upward.
    /// </remarks>
    internal static Histogram<long> CachedInputTokens { get; } =
        Meter.CreateHistogram<long>("agentinfer.tokens.cached_input", "{token}", "Cached input tokens per generation method call.");

    /// <summary>
    /// Spans, sharing the meter's name so one string enables everything.
    /// </summary>
    /// <remarks>
    /// One source rather than one per type. A second <see cref="ActivitySource"/>
    /// with the same name works, because listeners match on the name — which is
    /// exactly why having two is a trap: they behave identically until somebody
    /// renames one.
    /// </remarks>
    internal static ActivitySource Source { get; } = new(MeterName);

    /// <summary>
    /// Generation method calls in one <see cref="AgentScope"/>.
    /// </summary>
    /// <remarks>
    /// The workflow-level counterpart to <see cref="CallsPerTurn"/>, and the
    /// same argument one level up: an orchestration is worth what it costs, and
    /// a router that spends four model calls choosing between two branches is
    /// something to read rather than infer.
    /// </remarks>
    internal static Histogram<int> WorkflowOperations { get; } =
        Meter.CreateHistogram<int>("agentinfer.workflow.operations", "{call}", "Generation method calls per workflow.");

    /// <summary>Requests actually sent to a model in one scope.</summary>
    internal static Histogram<int> WorkflowRequests { get; } =
        Meter.CreateHistogram<int>("agentinfer.workflow.requests", "{request}", "Model requests per workflow.");

    /// <summary>Wall-clock seconds one scope took.</summary>
    internal static Histogram<double> WorkflowDuration { get; } =
        Meter.CreateHistogram<double>("agentinfer.workflow.duration", "s", "Workflow duration.");

    /// <summary>Input tokens one scope spent, across every agent in it.</summary>
    /// <remarks>
    /// The workflow-level counterpart to <see cref="InputTokens"/>. Not a sum
    /// anyone can take from that one after the fact: per-operation histograms
    /// are aggregated across every workflow that called the method, so there is
    /// no way back from them to what one composition cost.
    /// </remarks>
    internal static Histogram<long> WorkflowInputTokens { get; } =
        Meter.CreateHistogram<long>("agentinfer.workflow.tokens.input", "{token}", "Input tokens per workflow.");

    /// <summary>Output tokens one scope spent, across every agent in it.</summary>
    internal static Histogram<long> WorkflowOutputTokens { get; } =
        Meter.CreateHistogram<long>("agentinfer.workflow.tokens.output", "{token}", "Output tokens per workflow.");
}
