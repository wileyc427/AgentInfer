using Agentry;

namespace Incident;

/// <summary>What to do about an alert. A closed set, so the caller can switch.</summary>
/// <remarks>
/// The reason routing wants an enum rather than a string: the model's options
/// and the caller's branches are the same list, checked by the compiler. Add a
/// member and every <c>switch</c> over it stops compiling until somebody
/// decides what the new case does — which is the review step a string answer
/// silently skips.
/// </remarks>
public enum Severity
{
    /// <summary>Noise. Say so and stop.</summary>
    Ignore,

    /// <summary>Worth looking at now, by a machine.</summary>
    Investigate,

    /// <summary>Worth waking somebody for.</summary>
    Page,
}

/// <summary>Who owns the thing that broke.</summary>
public enum Team
{
    Platform,
    Data,
    Frontend,

    /// <summary>
    /// Deliberately a member rather than a null.
    /// </summary>
    /// <remarks>
    /// A model that cannot tell has to be able to say so. Without this it picks
    /// the most plausible-sounding team, the page goes to people who cannot fix
    /// it, and the answer is indistinguishable from a confident correct one.
    /// </remarks>
    Unknown,
}

/// <summary>What one service's telemetry turned out to show.</summary>
/// <remarks>
/// Agentry's own bounds rather than DataAnnotations, and the reason is
/// specific: <c>MaxLengthAttribute</c>'s constructor carries
/// <c>[RequiresUnreferencedCode]</c>, because <c>ValidationAttribute.IsValid</c>
/// inspects arbitrary types. Writing one makes the assembly that holds this
/// record unverifiable under trimming even when nothing reflects over it.
/// <c>[Range]</c> and <c>[MaxLength]</c> are still read and still work — they
/// are the right choice when the assembly is not a trimming target.
/// </remarks>
public sealed record Finding(
    string Service,
    bool Healthy,
    [property: Bounded(0, 100)] int Confidence,
    [property: Sized(Max = 400)] string Evidence);

/// <summary>The critic's verdict on a draft. A contract, not prose.</summary>
public sealed record Review(
    bool Approved,
    [property: Bounded(1, 5)] int Score,
    string[] Problems);

/// <summary>
/// Routing: two closed questions, answered separately.
/// </summary>
/// <remarks>
/// Separate methods rather than one returning a record with both, because the
/// two are asked at different moments and only one of them is always needed —
/// an alert classified <c>Ignore</c> never needs an owner, and paying a model
/// to name one is the cheapest kind of waste to leave in.
/// </remarks>
[Agent("""
    You triage production alerts for an on-call rotation.

    Be conservative about waking people. Page only when the alert describes
    user-visible failure or data loss that is already happening. Prefer
    Investigate when something looks wrong but nobody is hurt yet, and Ignore
    for anything that is noise, a known-flaky check, or already recovered.

    Answer with exactly one of the allowed values. Nothing else.
    """)]
public interface ITriage
{
    [Prompt("How should this alert be handled?")]
    public Task<Severity> SeverityAsync(string alert, CancellationToken ct = default);

    [Prompt("Which team owns the failing component? Answer Unknown if the alert does not say.")]
    public Task<Team> OwnerAsync(string alert, CancellationToken ct = default);
}

/// <summary>
/// One worker. Fanned out with <c>Task.WhenAll</c> and nothing else.
/// </summary>
/// <remarks>
/// Parallel sectioning needs no framework support: the agent is an interface,
/// the interface is thread-safe because the runner holds no per-call state, and
/// <c>Task.WhenAll</c> already exists. What it does need is a bound, which is
/// why the sample runs the fan-out inside an <c>AgentScope</c> — sixteen
/// branches at six iterations each is ninety-six requests nobody authorized.
/// </remarks>
[Agent("""
    You investigate one service during an incident.

    Use the telemetry tools. Never state a number you did not get from a tool,
    and never guess at a service you were not asked about.

    Report what the telemetry shows, and say plainly when it shows nothing
    wrong. A clean service is a useful finding.
    """,
    Tools = typeof(Telemetry))]
public interface IServiceInvestigator
{
    [Prompt("Investigate this service and report what its telemetry shows.")]
    [Strategy(Strategies.Predict, MaxIterations = 6)]
    public Task<Finding> InvestigateAsync(string service, CancellationToken ct = default);
}

/// <summary>
/// The evaluator-optimizer pair, on one interface.
/// </summary>
/// <remarks>
/// <para>
/// Writer and critic are two methods rather than two agents on purpose. They
/// share a system prompt — the same standard for what a postmortem has to
/// contain — and splitting them would mean maintaining that standard twice and
/// discovering the drift as a critic that rejects everything.
/// </para>
/// <para>
/// The loop is a <c>for</c> with a bound. It reads as a loop, steps in a
/// debugger as a loop, and the bound is a number a reviewer can see, which is
/// the whole argument against a declarative surface for this pattern.
/// </para>
/// </remarks>
[Agent("""
    You write and review incident postmortems.

    A postmortem states what broke, what the evidence was, and what is still
    unknown. It never speculates about cause beyond what the findings support,
    and it never states a figure that is not in the findings.

    When reviewing, reject a draft that asserts anything the findings do not
    show — including a cause. Say which sentence is the problem.
    """)]
public interface IPostmortem
{
    /// <remarks>
    /// On "accurate" because this is the method that has to reason over the
    /// findings rather than classify one sentence. Same instinct as the ledger
    /// sample: fetching the data was never the hard part.
    /// </remarks>
    [Prompt("Draft a postmortem for this incident from these findings.")]
    [Model(ModelRoles.Accurate)]
    public Task<string> DraftAsync(string alert, string findings, CancellationToken ct = default);

    [Prompt("Revise this draft so it answers every problem listed. Change nothing else.")]
    [Model(ModelRoles.Accurate)]
    public Task<string> ReviseAsync(string draft, string problems, CancellationToken ct = default);

    [Prompt("Is every claim in this draft supported by these findings?")]
    public Task<Review> ReviewAsync(string draft, string findings, CancellationToken ct = default);
}

/// <summary>
/// Orchestrator-workers, where the orchestrator is itself an agent.
/// </summary>
/// <remarks>
/// <para>
/// The workers reach it as <em>tools</em>: <see cref="Investigators"/> wraps
/// <see cref="IServiceInvestigator"/> in a method carrying
/// <see cref="AgentToolAttribute"/>, and the generator neither knows nor cares
/// that the body starts another agent. That works today because a tool may
/// return <c>Task&lt;T&gt;</c>, and it is the cheapest multi-agent story
/// available: no registry, no capability lookup, no new attribute.
/// </para>
/// <para>
/// What it buys over the fan-out above is <em>dynamic</em> decomposition — the
/// commander decides which services are worth looking at. What it costs is the
/// ability to know that in advance, and a nested budget. Both are visible in
/// <c>Workflows.CommandAsync</c>, which is the comparison worth making before
/// reaching for this shape.
/// </para>
/// </remarks>
[Agent("""
    You are the incident commander.

    Work out which services are worth investigating, investigate them, and say
    what is wrong in two or three sentences.

    You may only report what an investigation returned. If you could not reach
    a service, say so rather than inferring its state from its neighbors.
    """,
    Tools = typeof(Investigators))]
public interface IIncidentCommander
{
    [Prompt("Take command of this alert and report what is wrong.")]
    [Strategy(Strategies.Predict, MaxIterations = 8)]
    public Task<string> CommandAsync(string alert, CancellationToken ct = default);
}
