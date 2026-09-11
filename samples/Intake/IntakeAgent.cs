using System.ComponentModel.DataAnnotations;
using Agentry;

namespace Intake;

/// <summary>What a ticket turns out to be about. A closed set, so callers switch.</summary>
public enum Category { Billing, Technical, Account, Spam }

/// <summary>The structured read of one ticket.</summary>
/// <remarks>
/// No <c>[Range]</c> on Urgency any more. The bound moved into
/// <see cref="ExtractContract"/>, beside the schema that declares it to the
/// model — one read of one file to check they agree. DataAnnotations would
/// have been enforced reflectively, which under trimming can find nothing to
/// check and report success.
/// </remarks>
public sealed record Extract(
    string Customer,
    Category Category,
    int Urgency,
    string[] Asks);

/// <summary>
/// The contract. An ordinary interface, with no attributes on it.
/// </summary>
/// <remarks>
/// Worth noticing what is missing: no <c>[Agent]</c>, no <c>[Prompt]</c>, no
/// <c>[Model]</c>. Consumers still inject this and still mock it with no
/// framework support, because that property came from it being an interface and
/// never from the attributes.
/// </remarks>
public interface IIntake
{
    public Task<Category> ClassifyAsync(string ticket, CancellationToken ct = default);

    public Task<string> SummarizeAsync(string ticketId, CancellationToken ct = default);

    public Task<Extract> ExtractAsync(string ticketId, CancellationToken ct = default);
}

/// <summary>
/// A hand-written agent over generated tools.
/// </summary>
/// <remarks>
/// <para>
/// Everything here could have been generated, and three of the methods could
/// not have been — which is the point. An attribute argument must be a
/// compile-time constant, so a prompt assembled from a tenant's policy, a model
/// role chosen from the size of the input, and a retry that feeds a binding
/// failure back to the model are all outside what <c>[Agent]</c> can say. Bending
/// them into attributes would be worse than writing the class.
/// </para>
/// <para>
/// <b>What this costs, stated plainly,</b> because the comparison is only
/// useful if both columns are honest:
/// </para>
/// <list type="bullet">
///   <item>The response schema below is written by hand. It is derived from
///   <see cref="Extract"/> and nothing checks that it still matches — the one
///   line in this file that rots.</item>
///   <item>No <c>AgentryRoles.All</c>. That array is generated from
///   <c>[Model]</c> attributes, and there are none here, so the roles are
///   declared in <see cref="IntakeRoles"/> and startup validation checks what
///   somebody remembered to put there.</item>
///   <item>The trimming annotations are written out rather than emitted — and
///   propagated rather than suppressed, which the generated path does not yet
///   do.</item>
///   <item>No AGT001–AGT004: nothing checks that a prompt is non-empty or that
///   the return type is one the runtime can bind. Most of those rules police
///   hazards the attributes introduce, but not all.</item>
/// </list>
/// <para>
/// What it does not cost is anything to do with tools. Schemas, permissions and
/// dispatch are still compile-time constants in generated code, still free of
/// reflection, and still survive trimming.
/// </para>
/// </remarks>
public sealed class IntakeAgent : IIntake
{
    private readonly IModelRouter _router;
    private readonly IntakeToolsInvoker _tools;
    private readonly IToolAuthorizer _authorizer;
    private readonly string _systemPrompt;

    /// <param name="policy">
    /// The tenant's own rules, which decide part of the prompt.
    /// </param>
    /// <remarks>
    /// This constructor is the whole argument for writing the class. The prompt
    /// is assembled here, at run time, from something that differs per tenant
    /// and changes without a deploy. <c>[Agent("…")]</c> takes a constant, and
    /// <c>PromptFile</c> takes a file read at compile time — neither can say
    /// "whatever this customer's contract says today".
    /// </remarks>
    public IntakeAgent(
        IModelRouter router,
        IntakeToolsInvoker tools,
        IToolAuthorizer authorizer,
        TenantPolicy policy)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));

        _systemPrompt = $"""
            You triage inbound support tickets for {policy.Tenant}.

            Use the tools to read a ticket. Never state a fact about a customer
            that a tool did not report, and never invent a charge or a date.

            This tenant's escalation rules:
            {policy.Rules}
            """;
    }

    /// <summary>
    /// Routing, with the model chosen from the input.
    /// </summary>
    /// <remarks>
    /// <c>[Model]</c> names one role for all calls to a method. Most short
    /// tickets are one obvious word and a small model gets them right; a long
    /// one with three complaints in it is a different problem and deserves a
    /// different model. That decision depends on the argument, so it cannot be
    /// an attribute.
    /// </remarks>
    public async Task<Category> ClassifyAsync(string ticket, CancellationToken ct = default)
    {
        var role = ticket.Length > 400 ? IntakeRoles.Accurate : IntakeRoles.Fast;

        var call = new AgentCall
        {
            SystemPrompt = _systemPrompt,
            TaskPrompt = "What is this ticket about?",
            Operation = "IIntake.ClassifyAsync",
            Arguments = [new KeyValuePair<string, string>("ticket", ticket)],
        };

        // The schema travels with the contract, so there is no second place to
        // keep it.
        return await _router.For(role)
            .CompleteJsonAsync(call, CategoryContract.Instance, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Prose over tools. The plain case, and it is one line.</summary>
    public async Task<string> SummarizeAsync(string ticketId, CancellationToken ct = default)
    {
        var call = new AgentCall
        {
            SystemPrompt = _systemPrompt,
            TaskPrompt = "Read this ticket and summarize what the customer is asking for, in two sentences.",
            Operation = "IIntake.SummarizeAsync",
            Arguments = [new KeyValuePair<string, string>("ticketId", ticketId)],
        };

        return await _router.For(IntakeRoles.Fast)
            .CompleteWithToolsAsync(call, _tools, _authorizer, 16, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A typed read, with one repair attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library will not do this for you, deliberately: a retry hidden
    /// inside a method that looks like one call is the same class of surprise
    /// as a strategy that silently executes generated code. So it lives where a
    /// reviewer can see it — in the caller's own loop, with the bound visible.
    /// </para>
    /// <para>
    /// It works because the failed attempt carries what the model actually said
    /// and why it would not bind, which is exactly the sentence a model needs
    /// in order to fix its own reply. A second attempt and no more.
    /// </para>
    /// </remarks>
    public async Task<Extract> ExtractAsync(string ticketId, CancellationToken ct = default)
    {
        var call = new AgentCall
        {
            SystemPrompt = _systemPrompt,
            TaskPrompt = "Read this ticket and extract the customer, the category, how urgent it is, and what they are asking for.",
            Operation = "IIntake.ExtractAsync",
            Arguments = [new KeyValuePair<string, string>("ticketId", ticketId)],
        };

        var attempt = await ExtractOnceAsync(call, ct).ConfigureAwait(false);
        if (attempt.Succeeded) return attempt.Value;

        // Hand the failure back as an argument. Every call is a fresh
        // two-message request, so what the model is reacting to is exactly what
        // is on this line rather than a conversation that drifted.
        var repair = call with
        {
            Operation = call.Operation + " (repair)",
            Arguments =
            [
                .. call.Arguments,
                new KeyValuePair<string, string>("previousAttemptFailed", attempt.Problem!),
            ],
        };

        // The second attempt throws if it fails too. A model that has missed
        // the same schema twice is not going to get it on the third, and the
        // caller waiting on an answer should hear that rather than a third bill.
        return await _router.For(IntakeRoles.Accurate)
            .CompleteJsonWithToolsAsync(repair, ExtractContract.Instance, _tools, _authorizer, 12, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One attempt, reported rather than thrown.
    /// </summary>
    /// <remarks>
    /// A model answering <c>urgency: 9</c> against a declared 1–5 has not
    /// malfunctioned — it has done something this method anticipates and
    /// handles. Treating that as an exception made every ordinary run report a
    /// first-chance exception in a debugger, which reads as a failure and is
    /// not one. <c>if (attempt.Succeeded)</c> says "this may not have worked";
    /// <c>try</c>/<c>catch</c> says "this went wrong".
    /// </remarks>
    private Task<ReplyAttempt<Extract>> ExtractOnceAsync(AgentCall call, CancellationToken ct) =>
        _router.For(IntakeRoles.Accurate)
            .TryCompleteJsonWithToolsAsync(call, ExtractContract.Instance, _tools, _authorizer, 12, ct);
}

/// <summary>
/// The roles this app asks for.
/// </summary>
/// <remarks>
/// Hand-written in both halves here, and that is a real loss. With
/// <c>[Model]</c> attributes the generator emits <c>AgentryRoles.All</c> from
/// the roles actually asked for, so a role nobody registered is a startup
/// failure. Written by hand, <see cref="All"/> is what somebody remembered to
/// put in it — and a role used in a method but missing from this array
/// validates clean and fails on the call that needs it.
/// </remarks>
public static class IntakeRoles
{
    public const string Fast = "fast";

    public const string Accurate = "accurate";

    public static readonly string[] All = [Fast, Accurate];
}

/// <summary>A tenant's own escalation rules, loaded at run time.</summary>
public sealed record TenantPolicy(string Tenant, string Rules);
