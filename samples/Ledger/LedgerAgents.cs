using Agentry;

namespace Ledger;

/// <summary>What the critic hands back. A contract, not prose.</summary>
public sealed record Verdict(bool Approved, int Score, string[] Problems);

/// <summary>
/// The whole authoring surface: an interface, attributed.
/// </summary>
/// <remarks>
/// Note what the methods do <em>not</em> take. Before there were tools,
/// <c>SummariseAsync</c> was handed the figures as a string. Now the agent
/// fetches them, which is the point of tools and also the more honest demo —
/// passing the data in makes the tools decorative.
/// </remarks>
[Agent("""
    You answer questions about a household ledger.

    You have tools for listing categories, totalling one, and reading its
    budget. Use them. Never invent an amount, and never state a figure you did
    not get from a tool.

    Answer in one or two sentences with the actual numbers in them.
    """,
    Tools = typeof(LedgerTools))]
public interface ILedgerAnalyst
{
    /// <summary>Returns prose, so no schema and no parsing is involved.</summary>
    [Prompt("Which categories are over budget, and by how much?")]
    [Strategy(Strategies.Predict, MaxIterations = 8)]
    public Task<string> SummariseAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a record, so the reply is bound to the type. Tools resolve
    /// first; what is bound is the final message.
    /// </summary>
    [Prompt("Judge whether this summary is supported by the figures the tools report.")]
    public Task<Verdict> ReviewAsync(string summary, CancellationToken ct = default);
}
