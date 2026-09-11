using Agentry;

namespace Intake;

/// <summary>
/// The generated half, and the half worth generating.
/// </summary>
/// <remarks>
/// <para>
/// <c>[AgentTools]</c> emits <c>IntakeToolsInvoker</c>: a JSON Schema per tool
/// built from the parameter types, the permission list collected from the
/// attributes, and a dispatch switch that reads each argument with an accessor
/// chosen at compile time. Roughly ninety lines here, none of which anybody
/// would enjoy keeping in step with these signatures by hand.
/// </para>
/// <para>
/// No agent is attached. The class that calls these lives in
/// <see cref="IntakeAgent"/> and is written by hand — which is the comparison
/// this sample exists to make.
/// </para>
/// </remarks>
[AgentTools]
public sealed class IntakeTools
{
    private readonly record struct Entry(string Customer, string Body, string Plan);

    private readonly Dictionary<string, Entry> _tickets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["T-1041"] = new(
            "Ravensmere Dental",
            "We were charged twice for the March seat license and once for a seat we removed in "
            + "February. Need both refunded and the seat count corrected before the next cycle.",
            "Practice"),
        ["T-1042"] = new(
            "Halloway Freight",
            "Export to CSV times out on anything over about 8,000 rows. Worked last week.",
            "Enterprise"),
        ["T-1043"] = new(
            "—",
            "CONGRATULATIONS your account has been selected click here to claim",
            "None"),
    };

    [AgentTool("Every ticket currently waiting, oldest first.")]
    [RequiresPermission("intake.read")]
    public string[] Waiting() => [.. _tickets.Keys.Order()];

    [AgentTool("The full text of one ticket, with the customer and their plan.")]
    [RequiresPermission("intake.read")]
    public string Ticket(string id) =>
        _tickets.TryGetValue(id, out var ticket)
            ? $"customer: {ticket.Customer}\nplan: {ticket.Plan}\n\n{ticket.Body}"
            : $"No ticket '{id}'.";

    [AgentTool("What this customer has been charged in the last three cycles.")]
    [RequiresPermission("intake.billing")]
    public string Charges(string customer) =>
        throw new InvalidOperationException("Nothing in this sample holds intake.billing.");

    /// <summary>
    /// Closing a ticket. Never offered here.
    /// </summary>
    /// <remarks>
    /// The gate is not decoration at this method. The demo caller holds
    /// <c>intake.read</c> and nothing else, so a model reasoning about a
    /// ticket that looks like spam is never told that closing it is an
    /// option — absent from the menu, not refused after asking.
    /// </remarks>
    [AgentTool("Close a ticket without a reply.")]
    [RequiresPermission("intake.write")]
    public string Close(string id) =>
        throw new InvalidOperationException("Nothing in this sample holds intake.write.");
}
