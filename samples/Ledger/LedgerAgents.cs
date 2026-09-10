using Agentry;

namespace Ledger;

/// <summary>What the critic hands back. A contract, not prose.</summary>
public sealed record Verdict(bool Approved, int Score, string[] Problems);

/// <summary>
/// The whole authoring surface: an interface, attributed. No base class, no
/// registration call, no partial method to fill in.
/// </summary>
[Agent("""
    You answer questions about a household ledger.
    Use only figures you were given. Never invent an amount.
    Answer in one or two sentences with the actual numbers in them.
    """)]
public interface ILedgerAnalyst
{
    /// <summary>Returns prose, so no schema and no parsing is involved.</summary>
    [Prompt("Summarise this spending against its budget in two sentences.")]
    public Task<string> SummariseAsync(string spending, CancellationToken ct = default);

    /// <summary>
    /// Returns a record, so the generator routes it through the JSON path and
    /// the reply is bound to the type rather than handed back as text.
    /// </summary>
    [Prompt("Judge whether this summary is supported by the figures given.")]
    public Task<Verdict> ReviewAsync(string summary, string figures, CancellationToken ct = default);
}
