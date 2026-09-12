using System.Diagnostics;

namespace AgentInfer;

/// <summary>
/// One workflow, for the purpose of counting it.
/// </summary>
/// <remarks>
/// <para>
/// Workflow patterns here are ordinary C# over generated interfaces, which
/// leaves nowhere to hang a number: six method calls composed with
/// <c>await</c> and <c>switch</c> emit six unrelated spans, and "what did that
/// whole thing cost" has no answer. A scope starts an <see cref="Activity"/>
/// the per-call spans nest under, counts what passes through it, and records
/// three instruments when it closes.
/// </para>
/// <para>
/// Opened without a bound it changes nothing, which is what makes an ambient
/// <see cref="AsyncLocal{T}"/> object acceptable here. Given a bound it also
/// stops the workflow: <c>MaxIterations</c> bounds one method's tool loop,
/// nothing bounded the composition. The bound is declared at the call site, in
/// a <c>using</c> a reviewer reads before the work it governs; the ambient part
/// is only how it reaches the client.
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
    /// Throws rather than truncating. A workflow cut off at its bound writes a
    /// plausible answer from whatever it managed to gather, which cannot be
    /// told apart from one that finished.
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
    /// Counts roll up, so an agent reached as another agent's tool cannot hide
    /// its calls one level down.
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
    /// client rather than one a host registers, so a missed line of DI cannot
    /// leave the bound silently doing nothing.
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
    /// clearing the slot, so a nested scope closing does not detach the rest of
    /// the outer workflow from its counts.
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
    /// Checked before the request is sent: the point of a budget is the call
    /// that does not happen. Innermost first, so the message names the scope
    /// whose bound ran out.
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
/// Distinct from <see cref="AgentException"/>, which names one failed
/// operation. This one is about the composition: every operation may have
/// worked and the whole still cost more than it was given.
/// </remarks>
public sealed class AgentBudgetExceededException(string workflow, int maxRequests)
    : Exception($"'{workflow}' reached its bound of {maxRequests} model request(s) and stopped. "
                + "Raise the bound, or find the loop — a workflow that needed more than it was given "
                + "usually has a step trading turns with a tool.")
{
    public string Workflow { get; } = workflow;

    public int MaxRequests { get; } = maxRequests;
}
