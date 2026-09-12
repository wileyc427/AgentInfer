using System.Diagnostics.Metrics;

namespace Incident;

/// <summary>
/// Collects the AgentInfer meter and prints it when the run ends.
/// </summary>
/// <remarks>
/// <para>
/// Without a listener, every instrument in this library records into nothing.
/// <c>agentinfer.tool.calls_per_turn</c> is the number the README says decides
/// whether a workload ever needs the model to write code, and until something
/// subscribes it is an intention rather than a measurement.
/// </para>
/// <para>
/// Ten lines and no dependency, because the instruments are
/// <c>System.Diagnostics.Metrics</c> — the same source an OpenTelemetry
/// exporter would read. A real host points OTel at the <c>AgentInfer</c> meter and
/// gets this and much more; a sample wants to see the numbers without standing
/// up a collector.
/// </para>
/// </remarks>
internal sealed class Meters : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, List<double>> _readings = [];
    private readonly Lock _gate = new();

    public Meters()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "AgentInfer") listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Record(instrument.Name, value));
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument.Name, value));
        _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument.Name, value));

        _listener.Start();
    }

    private void Record(string instrument, double value)
    {
        lock (_gate)
        {
            if (!_readings.TryGetValue(instrument, out var values)) _readings[instrument] = values = [];
            values.Add(value);
        }
    }

    /// <summary>
    /// Every instrument, with the shape of its distribution.
    /// </summary>
    /// <remarks>
    /// p95 rather than a mean, because the question these answer is about the
    /// bad case. A workload averaging two tool calls a turn and reaching
    /// fifteen at the top of its range is a workload where generated code would
    /// buy something, and a mean hides that entirely.
    /// </remarks>
    public void Report()
    {
        lock (_gate)
        {
            if (_readings.Count == 0)
            {
                Console.WriteLine("  (no measurements — nothing called a generation method)");
                return;
            }

            Console.WriteLine("  instrument                       n      min    p50    p95    max");

            foreach (var (name, values) in _readings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var sorted = values.Order().ToArray();

                Console.WriteLine(
                    $"  {name,-30} {sorted.Length,4}   {sorted[0],5:0.#}  "
                    + $"{At(sorted, 0.50),5:0.#}  {At(sorted, 0.95),5:0.#}  {sorted[^1],5:0.#}");
            }
        }
    }

    private static double At(double[] sorted, double quantile) =>
        sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)];

    public void Dispose() => _listener.Dispose();
}
