using AgentInfer;

namespace Ledger;

/// <summary>One category's spend against its budget.</summary>
public sealed record CategorySummary(string Category, decimal Spent, decimal Budget);

/// <summary>
/// The deterministic half: ordinary methods with real bodies.
/// </summary>
/// <remarks>
/// Nothing here is exposed to a model because it is public. <c>Categories</c>
/// and <c>TotalFor</c> carry <see cref="AgentToolAttribute"/> because somebody
/// decided they should; <c>DebugDump</c> does not, and is therefore invisible
/// and unreachable — rather than merely undocumented and still callable.
/// </remarks>
public sealed class LedgerTools
{
    private readonly Dictionary<string, decimal> _spend = new()
    {
        ["groceries"] = 259.65m,
        ["coffee"] = 22.80m,
        ["transport"] = 57.75m,
        ["books"] = 31.99m,
    };

    private readonly Dictionary<string, decimal> _budget = new()
    {
        ["groceries"] = 300.00m,
        ["coffee"] = 15.00m,
        ["transport"] = 80.00m,
        ["books"] = 40.00m,
    };

    [AgentTool("Every category that has at least one transaction.")]
    [RequiresPermission("ledger.read")]
    public string[] Categories() => [.. _spend.Keys.Order()];

    [AgentTool("The total spent in one category.")]
    [RequiresPermission("ledger.read")]
    public decimal TotalFor(string category) => _spend.TryGetValue(category, out var total) ? total : 0m;

    [AgentTool("The monthly budget for a category. Zero when it has none.")]
    [RequiresPermission("ledger.read")]
    public decimal BudgetFor(string category) => _budget.TryGetValue(category, out var budget) ? budget : 0m;

    [AgentTool("Move a transaction into a different category.")]
    [RequiresPermission("ledger.write")]
    public void Reclassify(string category, string newCategory) =>
        throw new NotImplementedException("P2: the broker dispatches this, not the model directly.");

    /// <summary>
    /// Everything, in one call. The coarse-grained alternative.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three tools above are a chatty API: answering "which categories are
    /// over budget" through them costs 1 + 2N calls — nine for four categories,
    /// each one a separate model turn. This costs one.
    /// </para>
    /// <para>
    /// That difference is worth understanding before reaching for anything more
    /// exotic. The usual argument for letting a model write code is that it can
    /// loop over tools instead of calling them one at a time — but most of the
    /// time the loop exists because the tool API was designed for a UI, where a
    /// caller knows which single row it wants. A model asking an open question
    /// wants the whole table.
    /// </para>
    /// <para>
    /// Design tools for a caller that is reasoning about all of it at once, and
    /// the round trips that motivated generated code stop existing.
    /// </para>
    /// </remarks>
    [AgentTool("Every category with its total and its budget, in one call. Prefer this over the per-category tools.")]
    [RequiresPermission("ledger.read")]
    public IReadOnlyList<CategorySummary> Overview() =>
    [
        .. _spend.Keys.Order().Select(category => new CategorySummary(
            category,
            _spend[category],
            _budget.TryGetValue(category, out var budget) ? budget : 0m)),
    ];

    /// <summary>Not a tool. No attribute, so the generator never sees it.</summary>
    public string DebugDump() => string.Join(", ", _spend.Select(pair => $"{pair.Key}={pair.Value}"));
}
