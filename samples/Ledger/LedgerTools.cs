using Agentry;

namespace Ledger;

/// <summary>
/// The deterministic half: ordinary methods with real bodies.
/// </summary>
/// <remarks>
/// Nothing here is exposed to a model because it is public. <c>Categories</c>
/// and <c>TotalFor</c> carry <see cref="AgentToolAttribute"/> because somebody
/// decided they should; <c>DebugDump</c> does not, and is therefore invisible
/// and unreachable — not hidden from the documentation while remaining
/// callable, which is what the Python framework's <c>@hidden</c> actually does.
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

    /// <summary>Not a tool. No attribute, so the generator never sees it.</summary>
    public string DebugDump() => string.Join(", ", _spend.Select(pair => $"{pair.Key}={pair.Value}"));
}
