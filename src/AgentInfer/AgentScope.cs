using System.Diagnostics;

namespace AgentInfer;

/// <summary>
/// One workflow, for the purpose of counting it.
/// </summary>
/// <remarks>
/// <para>
/// This library's argument is that the workflow patterns — chaining, routing,
/// fanning out, a writer and a critic trading turns — are ordinary C# over
/// generated interfaces and need no framework surface. That holds. What it
/// leaves missing is a place to hang a number on: six generation method calls
/// composed with <c>await</c> and <c>switch</c> emit six unrelated spans, and
/// "what did that whole thing cost" has no answer.
/// </para>
/// <para>
/// A scope starts an <see cref="Activity"/> the per-call spans nest under,
/// counts what passed through it, and records three instruments when it
/// closes. Opened without a bound it changes nothing: code inside one runs
/// identically to code outside one, which is what makes an ambient,
/// <see cref="AsyncLocal{T}"/>-flowed object acceptable in a library that
/// otherwise insists on saying things out loud.
/// </para>
/// <para>
/// <b>Given a bound it also stops the workflow.</b> <c>MaxIterations</c> bounds
/// one method's tool loop; nothing bounded the composition, so six methods at
/// sixteen iterations was ninety-six requests with no ceiling — and the
/// library's own line about a bound rather than a suggestion applies harder
/// here than it does one level down. The bound is declared at the call site,
/// in a <c>using</c> a reviewer reads before the work it governs. The ambient
/// part is only how it reaches the client.
/// </para>
/// <para>
/// The same discipline as <c>agentinfer.tool.calls_per_turn</c>, one level up.
/// That histogram answers "should the model be composing tool calls in code it
/// writes"; these answer "is this orchestration earning the calls it makes" —
/// and a router that turned out to cost four model calls to pick between two
/// branches is a thing you want to read rather than infer.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var scope = AgentScope.Begin("incident.triage");
///
/// var severity = await triage.ClassifySeverityAsync(alert);
/// var findings = await Task.WhenAll(services.Select(s => investigator.InvestigateAsync(s)));
/// var draft    = await writer.DraftAsync(findings);
///
/// logger.LogInformation("{Scope}", scope);   // 6 operations, 9 requests, 14 tool calls in 12.4s
/// </code>
/// </example>
public sealed class AgentScope : IDisposable
{
    private static readonly AsyncLocal<AgentScope?> Ambient = new();

    private readonly Activity? _activity;
    private readonly long _started;

    private int _operations;
    private int _requests;
    private int _toolCalls;
    private bool _closed;

    private AgentScope(string name, AgentScope? parent, int maxRequests)
    {
        Name = name;
        Parent = parent;
        MaxRequests = maxRequests;
        _started = Stopwatch.GetTimestamp();
        _activity = AgentMetrics.Source.StartActivity(name, ActivityKind.Internal);
    }

    /// <summary>The innermost scope on this execution context, if any.</summary>
    public static AgentScope? Current => Ambient.Value;

    /// <summary>Opens a scope. Dispose it to record what it cost.</summary>
    /// <param name="name">
    /// What the workflow is, in your words — <c>"incident.triage"</c>. It tags
    /// every instrument below, so it wants to be a name you would group by
    /// rather than a sentence.
    /// </param>
    public static AgentScope Begin(string name) => Begin(name, int.MaxValue);

    /// <summary>Opens a scope that will not send more than <paramref name="maxRequests"/>.</summary>
    /// <param name="name">What the workflow is, in your words.</param>
    /// <param name="maxRequests">
    /// The most model requests this workflow may send before it gives up.
    /// Counted across every agent, every tool round and every nested scope
    /// inside it.
    /// </param>
    /// <remarks>
    /// It throws rather than truncating, and that is the same decision
    /// <c>MaxIterations</c> got wrong first time round. Cutting a model off at
    /// its bound and keeping the answer produced prose that read fine and said
    /// "other categories lack sufficient data" about figures that were right
    /// there. A workflow that spent its budget has not answered the question,
    /// and saying so is the only outcome that cannot be mistaken for one that
    /// did.
    /// </remarks>
    public static AgentScope Begin(string name, int maxRequests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequests, 1);

        var scope = new AgentScope(name, Ambient.Value, maxRequests);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>The most requests this scope allows, or <see cref="int.MaxValue"/>.</summary>
    public int MaxRequests { get; }

    /// <summary>The scope this one opened inside, or null.</summary>
    /// <remarks>
    /// Counts roll up. An agent reached as another agent's tool does its work
    /// in a nested scope, and the outer number has to include it — otherwise
    /// the cheapest-looking orchestration is the one that hides its calls one
    /// level down.
    /// </remarks>
    public AgentScope? Parent { get; }

    public string Name { get; }

    /// <summary>Generation method calls made in this scope.</summary>
    /// <remarks>What the caller composed: one per <c>await</c> on an agent method.</remarks>
    public int Operations => Volatile.Read(ref _operations);

    /// <summary>
    /// Requests actually sent to a model.
    /// </summary>
    /// <remarks>
    /// Always at least <see cref="Operations"/> and usually more: a tool-using
    /// method is one operation and one request per round of tool calls, and a
    /// typed one adds the binding call.
    /// <para>
    /// Counted by a decorator <see cref="AgentRunner"/> puts around its own
    /// client, rather than by something a host has to remember to register.
    /// A bound that silently does nothing because a line of DI was missed is
    /// the exact failure this library exists to stop shipping.
    /// </para>
    /// </remarks>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Tool invocations across every operation in this scope.</summary>
    public int ToolCalls => Volatile.Read(ref _toolCalls);

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    /// <summary>
    /// Closes the scope and records it.
    /// </summary>
    /// <remarks>
    /// Restores whichever scope was current when this one opened rather than
    /// clearing the slot, so a nested scope closing does not silently detach
    /// the rest of the outer workflow from its own counts.
    /// </remarks>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;

        Ambient.Value = Parent;

        var tag = new KeyValuePair<string, object?>("workflow", Name);

        AgentMetrics.WorkflowOperations.Record(Operations, tag);
        AgentMetrics.WorkflowRequests.Record(Requests, tag);
        AgentMetrics.WorkflowDuration.Record(Elapsed.TotalSeconds, tag);

        _activity?.SetTag("agentinfer.workflow.operations", Operations);
        _activity?.SetTag("agentinfer.workflow.requests", Requests);
        _activity?.SetTag("agentinfer.workflow.tool_calls", ToolCalls);
        _activity?.Dispose();
    }

    /// <summary>`6 operations, 9 requests, 14 tool calls in 12.4s`.</summary>
    public override string ToString() =>
        $"{Name}: {Operations} operation(s), {Requests} request(s), {ToolCalls} tool call(s) in {Elapsed.TotalSeconds:F1}s";

    internal static void RecordOperation(int toolCalls)
    {
        for (var scope = Ambient.Value; scope is not null; scope = scope.Parent)
        {
            Interlocked.Increment(ref scope._operations);
            if (toolCalls > 0) Interlocked.Add(ref scope._toolCalls, toolCalls);
        }
    }

    /// <summary>
    /// Counts one request against every enclosing scope, innermost first.
    /// </summary>
    /// <remarks>
    /// Checked before the request is sent, because the point of a budget is the
    /// call that does not happen. Innermost first so the message names the
    /// scope whose bound actually ran out, which is the one worth reading.
    /// </remarks>
    internal static void RecordRequest()
    {
        AgentScope? tripped = null;

        for (var scope = Ambient.Value; scope is not null; scope = scope.Parent)
        {
            if (Interlocked.Increment(ref scope._requests) <= scope.MaxRequests) continue;

            tripped = scope;
            break;
        }

        if (tripped is null) return;

        // Unwind what this pass counted. The request is not going to be sent,
        // and a Requests that includes it would misreport the very number the
        // exception is asking somebody to act on.
        for (var scope = Ambient.Value; scope is not null; scope = scope.Parent)
        {
            Interlocked.Decrement(ref scope._requests);
            if (ReferenceEquals(scope, tripped)) break;
        }

        throw new AgentBudgetExceededException(tripped.Name, tripped.MaxRequests);
    }
}

/// <summary>Thrown when a workflow has sent every request it was allowed.</summary>
/// <remarks>
/// Distinct from <see cref="AgentException"/>, which names one operation that
/// could not produce a usable result. This one is about the composition: every
/// operation may have worked, and the whole still cost more than it was given.
/// </remarks>
public sealed class AgentBudgetExceededException(string workflow, int maxRequests)
    : Exception($"'{workflow}' reached its bound of {maxRequests} model request(s) and stopped. "
                + "Raise the bound, or find the loop — a workflow that needed more than it was given "
                + "usually has a step trading turns with a tool.")
{
    public string Workflow { get; } = workflow;

    public int MaxRequests { get; } = maxRequests;
}
