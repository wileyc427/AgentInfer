using Agentry;

namespace Incident;

/// <summary>
/// One agent, offered to another as tools.
/// </summary>
/// <remarks>
/// <para>
/// There is no framework feature here and that is the finding worth writing
/// down. <c>[AgentTool]</c> already accepts a method returning
/// <c>Task&lt;T&gt;</c>, so a method whose body happens to start another agent
/// is a tool like any other. Orchestrator-workers needs no registry, no
/// capability lookup and no new attribute — it needs a class.
/// </para>
/// <para>
/// The permission story comes along unchanged, and it is better here than it
/// looks in a ledger. <c>Page</c> wakes a human at three in the morning and
/// requires <c>incident.page</c>; a commander whose caller does not hold it is
/// never told that paging exists. Not refused after asking — absent.
/// </para>
/// <para>
/// The cost is worth stating: a sub-agent's calls are the commander's calls,
/// and the commander cannot see them. That is what
/// <see cref="AgentScope"/> nesting is for — the counts roll up, so an
/// orchestration cannot look cheap by hiding its work one level down.
/// </para>
/// </remarks>
public sealed class Investigators(IServiceInvestigator investigator, Telemetry telemetry)
{
    private readonly List<string> _paged = [];

    /// <summary>Who was paged, for the sample to print.</summary>
    public IReadOnlyList<string> Paged => _paged;

    [AgentTool("Every service reporting telemetry in the last hour.")]
    [RequiresPermission("incident.read")]
    public string[] Services() => telemetry.Services();

    /// <summary>Runs a whole agent, and reports what it found as text.</summary>
    /// <remarks>
    /// The nested scope is not decoration. Without it the sub-agent's requests
    /// land on whichever scope happens to be current, which is the right answer
    /// by accident; with it, <c>investigate:checkout-api</c> is a span of its
    /// own and its cost is attributable.
    /// </remarks>
    [AgentTool("Investigate one service and report what its telemetry shows.")]
    [RequiresPermission("incident.read")]
    public async Task<string> Investigate(string service, CancellationToken ct = default)
    {
        using var scope = AgentScope.Begin($"investigate:{service}");

        var finding = await investigator.InvestigateAsync(service, ct).ConfigureAwait(false);

        return $"{finding.Service}: {(finding.Healthy ? "healthy" : "UNHEALTHY")} "
             + $"(confidence {finding.Confidence}%) — {finding.Evidence}";
    }

    [AgentTool("Wake the on-call engineer for a team.")]
    [RequiresPermission("incident.page")]
    public string Page(Team team, string why)
    {
        _paged.Add($"{team}: {why}");
        return $"Paged {team}.";
    }
}
