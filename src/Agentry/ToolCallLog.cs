using System.Diagnostics.Metrics;

namespace Agentry;

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
/// p95 is the P3 decision.
/// </para>
/// </remarks>
public static class AgentMetrics
{
    /// <summary>The meter name to enable in an OpenTelemetry pipeline.</summary>
    public const string MeterName = "Agentry";

    internal static Meter Meter { get; } = new(MeterName);

    /// <summary>Every tool invocation, tagged by tool and outcome.</summary>
    internal static Counter<long> ToolCalls { get; } =
        Meter.CreateCounter<long>("agentry.tool.calls", "{call}", "Tool invocations.");

    /// <summary>Tool calls in one generation method call. The P3 decision.</summary>
    internal static Histogram<int> CallsPerTurn { get; } =
        Meter.CreateHistogram<int>("agentry.tool.calls_per_turn", "{call}", "Tool calls per generation method call.");

    /// <summary>Tools offered after permissions narrowed the menu.</summary>
    internal static Histogram<int> ToolsOffered { get; } =
        Meter.CreateHistogram<int>("agentry.tools.offered", "{tool}", "Tools offered to the model.");
}
