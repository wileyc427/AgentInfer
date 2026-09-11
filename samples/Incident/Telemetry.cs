using Agentry;

namespace Incident;

/// <summary>
/// The deterministic half. Ordinary methods over a canned incident.
/// </summary>
/// <remarks>
/// <para>
/// Shaped for a caller reasoning about all of it at once, which is the lesson
/// the ledger sample paid for. <c>ErrorRate</c> and <c>LatencyP99</c> per
/// service is a UI's API: answering "what is wrong" through them costs 1 + 2N
/// calls, and a model cut off at its iteration bound writes a confident summary
/// of the half it managed to fetch. <c>Snapshot</c> returns the table.
/// </para>
/// <para>
/// Both are offered anyway, because the comparison is the point: the
/// <c>agentry.tool.calls_per_turn</c> histogram is how you find out which one
/// a real model reaches for, and "design a better tool" is the fix that makes
/// the argument for generated code evaporate.
/// </para>
/// </remarks>
public sealed class Telemetry
{
    private readonly record struct Reading(double ErrorRate, int LatencyP99, string Note);

    private readonly Dictionary<string, Reading> _readings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["checkout-api"] = new(0.42, 8400, "5xx from the payments dependency since 02:10"),
        ["payments"] = new(0.51, 12100, "connection pool exhausted; upstream bank API timing out"),
        ["search"] = new(0.002, 180, "nominal"),
        ["recommendations"] = new(0.004, 240, "nominal"),
    };

    [AgentTool("Every service reporting telemetry in the last hour.")]
    [RequiresPermission("incident.read")]
    public string[] Services() => [.. _readings.Keys.Order()];

    [AgentTool("The fraction of requests failing for one service, 0 to 1.")]
    [RequiresPermission("incident.read")]
    public double ErrorRate(string service) =>
        _readings.TryGetValue(service, out var reading) ? reading.ErrorRate : 0d;

    [AgentTool("The 99th-percentile latency for one service, in milliseconds.")]
    [RequiresPermission("incident.read")]
    public int LatencyP99(string service) =>
        _readings.TryGetValue(service, out var reading) ? reading.LatencyP99 : 0;

    /// <summary>Every service with its error rate, latency and note. One call.</summary>
    [AgentTool("Every service with its error rate, latency and operator note, in one call.")]
    [RequiresPermission("incident.read")]
    public string Snapshot() =>
        string.Join("\n", _readings.OrderBy(pair => pair.Key).Select(pair =>
            $"{pair.Key}: errors={pair.Value.ErrorRate:P1} p99={pair.Value.LatencyP99}ms — {pair.Value.Note}"));

    /// <summary>
    /// Restarting something during an incident. Never offered here.
    /// </summary>
    /// <remarks>
    /// The permission gate stops being decorative at exactly this method. The
    /// demo caller holds <c>incident.read</c> and not <c>incident.restart</c>,
    /// so a model reasoning about a service with a full connection pool is
    /// never told that bouncing it is an option — it is absent from the menu,
    /// not refused after being asked for.
    /// </remarks>
    [AgentTool("Restart a service. Drops in-flight requests.")]
    [RequiresPermission("incident.restart")]
    public string Restart(string service) =>
        throw new InvalidOperationException("Nothing in this sample should reach this method.");
}
