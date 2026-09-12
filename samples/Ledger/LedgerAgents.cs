using System.ComponentModel.DataAnnotations;

using AgentInfer;

namespace Ledger;

/// <summary>What the critic hands back. A contract, not prose.</summary>
/// <remarks>
/// The <c>[Range]</c> is not decoration. Without it the schema says
/// <c>{"type":"integer"}</c>, and a real run answered <c>score: 100</c> out of
/// five — which bound cleanly, because 100 is a perfectly good integer. The
/// attribute now does double duty: it is written into the schema the model is
/// given, and it is checked after binding.
/// </remarks>
public sealed record Verdict(
    bool Approved,
    [property: Range(1, 5)] int Score,
    string[] Problems);

/// <summary>
/// The whole authoring surface: an interface, attributed.
/// </summary>
/// <remarks>
/// <para>
/// Note what the methods do <em>not</em> take. Before there were tools,
/// <c>SummarizeAsync</c> was handed the figures as a string. Now the agent
/// fetches them, which is the point of tools and also the more honest demo —
/// passing the data in makes the tools decorative.
/// </para>
/// <para>
/// The prompt is in <c>Prompts/ledger-analyst.md</c>, read by the generator at
/// compile time and emitted as the same constant an inline prompt produces —
/// open <c>obj/generated</c> and it is there in full. The sample takes this
/// route because a system prompt is the one string here that grows: markdown
/// beats a raw string literal for anything long enough to want headings, and
/// the diff lands on a prompt file rather than on a code file.
/// </para>
/// </remarks>
[Agent(PromptFile = "Prompts/ledger-analyst.md", Tools = typeof(LedgerTools))]
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
    /// <remarks>
    /// On the "smallllm" role because this is the method that has to reason, and
    /// the role a deployment repoints when it wants a better model here. A weak
    /// model given the same one-call Overview result still answers
    /// inconsistently, so fetching the data is not the hard part.
    /// </remarks>
    [Prompt("Which categories are over budget, and by how much?")]
    [Strategy(Strategies.Predict, MaxIterations = 16)]
    [Model(ModelRoles.SmallLlm)]
    public Task<string> SummarizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a record, so the reply is bound to the type. Tools resolve
    /// first; what is bound is the final message.
    /// </summary>
    [Prompt("Judge whether this summary is supported by the figures the tools report.")]
    public Task<Verdict> ReviewAsync(string summary, CancellationToken ct = default);
}
