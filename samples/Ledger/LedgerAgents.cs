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
    /// <remarks>
    /// MaxIterations was 8, and a real qwen3 run silently produced a wrong
    /// answer because of it: answering through the per-category tools needs
    /// 1 + 2N calls — nine for four categories — and the model is cut off at
    /// the bound, then writes a summary from what it managed to fetch. The
    /// output read as "other categories lack sufficient data", which is a
    /// plausible sentence and a false one.
    ///
    /// Raised to 16 so the chatty path completes. The better fix is the
    /// Overview tool, which does it in one call — see LedgerTools.
    /// </remarks>
    [Prompt("Which categories are over budget, and by how much?")]
    [Strategy(Strategies.Predict, MaxIterations = 16)]
    public Task<string> SummariseAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a record, so the reply is bound to the type. Tools resolve
    /// first; what is bound is the final message.
    /// </summary>
    [Prompt("Judge whether this summary is supported by the figures the tools report.")]
    public Task<Verdict> ReviewAsync(string summary, CancellationToken ct = default);
}
