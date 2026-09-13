using AgentInfer;

namespace Incident;

/// <summary>
/// The agentic workflow patterns, as ordinary C#.
/// </summary>
/// <remarks>
/// <para>
/// This file is the argument. Chaining is <c>await</c>. Routing is
/// <c>switch</c>. Parallel sectioning is <c>Task.WhenAll</c>. A writer and a
/// critic trading turns is <c>for</c>. None of it needed an attribute, a
/// builder, a graph or a registry — because an agent is an interface, and
/// composing interfaces is a thing the language already does well.
/// </para>
/// <para>
/// What a declarative surface would buy over this: a picture. What it would
/// cost: this file no longer steps in a debugger, the bound stops being a
/// number on a line somebody typed, and a failure arrives as a graph node id
/// rather than a stack trace. The library's own rule — a feature that cannot
/// be diagnosed at compile time should be questioned before it is added —
/// answers it. A workflow graph is no more checkable than a <c>for</c> loop
/// and strictly harder to read.
/// </para>
/// <para>
/// The one thing plain C# does <em>not</em> give you is the number at the
/// bottom, which is why every method here opens an <see cref="AgentScope"/>.
/// </para>
/// </remarks>
public sealed class Workflows(
    ITriage triage,
    IServiceInvestigator investigator,
    IPostmortem postmortem,
    IIncidentCommander commander,
    Telemetry telemetry)
{
    /// <summary>
    /// Routing. A closed return type becomes a branch the compiler checks.
    /// </summary>
    /// <remarks>
    /// The payoff of <c>Task&lt;Severity&gt;</c> over <c>Task&lt;string&gt;</c>
    /// is this <c>switch</c>. Add a <see cref="Severity"/> member and it stops
    /// compiling until somebody decides what the new case does. A string answer
    /// would have compiled, fallen through, and done nothing — at three in the
    /// morning.
    /// </remarks>
    public async Task<string> TriageAsync(string alert, CancellationToken ct = default)
    {
        // 20, and the number came from arithmetic rather than taste: two
        // classifications, then four services at three requests each — a typed
        // tool-using method is one round to call the tool, one to answer, and
        // one to bind. That is 14, and the first bound written here was 12,
        // which stopped the workflow on its first run. Which is the point: it
        // said so, rather than returning a triage decision made from half a
        // sweep.
        using var scope = AgentScope.Begin("incident.triage", maxRequests: 20);

        var severity = await triage.SeverityAsync(alert, ct);

        var outcome = severity switch
        {
            Severity.Ignore => "No action: the alert is noise.",

            Severity.Investigate => await SweepAsync(alert, ct),

            // Only this branch pays for a second classification. An alert
            // nobody is being woken for does not need an owner.
            Severity.Page => $"Paging {await triage.OwnerAsync(alert, ct)}. "
                           + await SweepAsync(alert, ct),

            _ => throw new ArgumentOutOfRangeException(nameof(alert), severity, "Unhandled severity."),
        };

        Console.WriteLine($"  {scope}");
        return $"[{severity}] {outcome}";
    }

    /// <summary>
    /// Parallel sectioning. Independent work, fanned out and rejoined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Task.WhenAll</c> over generated interfaces, and nothing else. The
    /// runner holds no per-call state, so an agent is safe to call concurrently
    /// — the counters on the scope are interlocked for exactly this.
    /// </para>
    /// <para>
    /// The bound matters more here than anywhere. Four services at six
    /// iterations each is twenty-four requests from one line, and the failure
    /// mode of getting it wrong is a bill rather than an exception.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Finding>> SectionAsync(CancellationToken ct = default)
    {
        using var scope = AgentScope.Begin("incident.sweep", maxRequests: 40);

        var findings = await Task.WhenAll(
            telemetry.Services().Select(service => investigator.InvestigateAsync(service, ct)));

        Console.WriteLine($"  {scope}");
        return findings;
    }

    /// <summary>
    /// Evaluator-optimizer. A writer and a critic, with a bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The critic's <see cref="Review"/> is a typed contract rather than prose,
    /// which is what makes <c>if (review.Approved) break;</c> possible at all.
    /// A critic that returned a paragraph would need a second model call to
    /// decide whether the paragraph meant yes.
    /// </para>
    /// <para>
    /// <c>attempts</c> is a bound on the conversation and the scope is a bound
    /// on the cost, and they are not the same bound: a revision that triggers a
    /// tool loop can spend far more than one request. Both, because they fail
    /// in different places.
    /// </para>
    /// </remarks>
    public async Task<string> PostmortemAsync(
        string alert,
        IReadOnlyList<Finding> findings,
        int attempts = 3,
        CancellationToken ct = default)
    {
        using var scope = AgentScope.Begin("incident.postmortem", maxRequests: 20);

        var evidence = string.Join("\n", findings.Select(f =>
            $"{f.Service}: {(f.Healthy ? "healthy" : "unhealthy")} — {f.Evidence}"));

        // Streamed rather than awaited whole, so the sample exercises the path
        // a web app would use. The scope counts this as one request, the same as
        // the buffered call it replaces — CountingChatClient wraps both.
        var written = new System.Text.StringBuilder();

        await foreach (var piece in postmortem.StreamDraftAsync(alert, evidence, ct))
        {
            written.Append(piece);
        }

        var draft = written.ToString();
        Console.WriteLine($"  drafted {draft.Length} characters, streamed");

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var review = await postmortem.ReviewAsync(draft, evidence, ct);

            Console.WriteLine(
                $"  review {attempt}: approved={review.Approved} score={review.Score}/5"
                + (review.Problems.Length == 0 ? string.Empty : $" — {string.Join("; ", review.Problems)}"));

            if (review.Approved) break;

            // The critique goes back in as an argument, not as conversation
            // history. Every call is a fresh two-message request, so what the
            // reviser is reacting to is exactly what is on this line — legible
            // in a log, and impossible to drift.
            draft = await postmortem.ReviseAsync(draft, string.Join("\n", review.Problems), ct);
        }

        Console.WriteLine($"  {scope}");
        return draft;
    }

    /// <summary>
    /// Orchestrator-workers. The model decides the decomposition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth comparing against <see cref="SectionAsync"/> rather than admiring
    /// on its own. Sectioning knows the work in advance, costs exactly what the
    /// list says, and cannot investigate something nobody listed. This decides
    /// at run time — and pays for the deciding, in requests you cannot predict
    /// and in a sub-agent whose calls are invisible from here.
    /// </para>
    /// <para>
    /// The scope is how you find out which one you actually wanted. If the
    /// commander reliably investigates everything, it bought nothing over
    /// <c>Task.WhenAll</c> and cost a model call to reach the same list.
    /// </para>
    /// </remarks>
    public async Task<string> CommandAsync(string alert, CancellationToken ct = default)
    {
        using var scope = AgentScope.Begin("incident.command", maxRequests: 30);

        var report = await commander.CommandAsync(alert, ct);

        Console.WriteLine($"  {scope}");
        return report;
    }

    /// <summary>Prompt chaining, which is the one that needs no explanation.</summary>
    private async Task<string> SweepAsync(string alert, CancellationToken ct)
    {
        var findings = await Task.WhenAll(
            telemetry.Services().Select(service => investigator.InvestigateAsync(service, ct)));

        var unhealthy = findings.Where(f => !f.Healthy).Select(f => f.Service).ToArray();

        return unhealthy.Length == 0
            ? "Every service is reporting clean."
            : $"Unhealthy: {string.Join(", ", unhealthy)}.";
    }
}
