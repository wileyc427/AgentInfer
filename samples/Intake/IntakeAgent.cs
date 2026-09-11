using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

using Agentry;

namespace Intake;

/// <summary>What a ticket turns out to be about. A closed set, so callers switch.</summary>
public enum Category { Billing, Technical, Account, Spam }

/// <summary>The structured read of one ticket.</summary>
public sealed record Extract(
    string Customer,
    Category Category,
    [property: Range(1, 5)] int Urgency,
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

    public Task<string> SummariseAsync(string ticketId, CancellationToken ct = default);

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

            // Hand-written, and derived from Category. Add a member and this
            // string is wrong until somebody remembers — the generator's job,
            // done by hand.
            ResponseSchema = """{"type":"string","enum":["billing","technical","account","spam"]}""",
        };

        return await Bind<Category>(role, call, ct).ConfigureAwait(false);
    }

    /// <summary>Prose over tools. The plain case, and it is one line.</summary>
    public async Task<string> SummariseAsync(string ticketId, CancellationToken ct = default)
    {
        var call = new AgentCall
        {
            SystemPrompt = _systemPrompt,
            TaskPrompt = "Read this ticket and summarise what the customer is asking for, in two sentences.",
            Operation = "IIntake.SummariseAsync",
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
    /// It works because <c>AgentException</c> carries what the model actually
    /// said and why it would not bind, which is exactly the sentence a model
    /// needs in order to fix its own reply. A second attempt and no more: a
    /// model that has failed twice on a schema is not going to succeed on the
    /// third, and the bound belongs next to the loop rather than in a setting.
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

            // The line that rots. Derived from Extract — including the [Range]
            // on Urgency and the camelCasing the binder expects — and nothing
            // checks it still agrees with the record. This is precisely what
            // [Agent] would have emitted, and precisely what you take on by
            // writing the class.
            ResponseSchema = """
                {"type":"object","properties":{"customer":{"type":"string"},"category":{"type":"string","enum":["billing","technical","account","spam"]},"urgency":{"type":"integer","minimum":1,"maximum":5},"asks":{"type":"array","items":{"type":"string"}}},"required":["customer","category","urgency","asks"],"additionalProperties":false}
                """,
        };

        try
        {
            return await Extract(call, ct).ConfigureAwait(false);
        }
        catch (AgentException error)
        {
            // Hand the failure back as an argument. Every call is a fresh
            // two-message request, so what the model is reacting to is exactly
            // what is on this line rather than a conversation that drifted.
            var repair = call with
            {
                Operation = call.Operation + " (repair)",
                Arguments =
                [
                    .. call.Arguments,
                    new KeyValuePair<string, string>("previousAttemptFailed", error.Message),
                ],
            };

            return await Extract(repair, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The typed call, kept in one place so the repair can reuse it.</summary>
    /// <remarks>
    /// Carries the runtime's own annotations rather than suppressing them.
    /// <c>CompleteJsonWithToolsAsync</c> is marked <c>[RequiresUnreferencedCode]</c>
    /// because it really does bind reflectively, and a method that calls it is
    /// in the same position — so it says so, and the warning travels to whoever
    /// publishes trimmed. An <c>UnconditionalSuppressMessage</c> here would
    /// assert the opposite: "analysed, and safe". It is not safe, and the
    /// suppression would stop the one signal that says so.
    /// </remarks>
    [RequiresUnreferencedCode("Binds the reply with reflection-based JSON.")]
    [RequiresDynamicCode("Binds the reply with reflection-based JSON.")]
    private Task<Extract> Extract(AgentCall call, CancellationToken ct) =>
        _router.For(IntakeRoles.Accurate)
            .CompleteJsonWithToolsAsync<Extract>(call, _tools, _authorizer, 12, null, ct);

    [RequiresUnreferencedCode("Binds the reply with reflection-based JSON.")]
    [RequiresDynamicCode("Binds the reply with reflection-based JSON.")]
    private Task<T> Bind<T>(string role, AgentCall call, CancellationToken ct) =>
        _router.For(role).CompleteJsonAsync<T>(call, null, ct);
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
