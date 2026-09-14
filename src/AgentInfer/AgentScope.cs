using System.Diagnostics;

using Microsoft.Extensions.AI;

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
    private long _inputTokens;
    private long _outputTokens;
    private long _totalTokens;
    private long _reasoningTokens;
    private long _cachedInputTokens;
    private int _usageReports;
    // An int rather than a bool so Dispose can claim it atomically. See there.
    private int _closed;

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

    /// <summary>
    /// What this scope spent, summed over every request in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number <see cref="Requests"/> cannot give. A workflow's requests are
    /// interchangeable only if its prompts are the same size, and the first
    /// thing anyone builds with tools is a step whose prompt grows by a tool
    /// result each round.
    /// </para>
    /// <para>
    /// Zero when nothing reported — see <see cref="UsageReported"/>, because a
    /// provider that says nothing and a call that cost nothing produce the same
    /// zeroes here and mean opposite things.
    /// </para>
    /// </remarks>
    public TokenCounts Tokens => new(
        Interlocked.Read(ref _inputTokens),
        Interlocked.Read(ref _outputTokens),
        Interlocked.Read(ref _totalTokens),
        Interlocked.Read(ref _reasoningTokens),
        Interlocked.Read(ref _cachedInputTokens));

    /// <summary>Whether any request in this scope reported its usage.</summary>
    public bool UsageReported => Volatile.Read(ref _usageReports) > 0;

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
        // Claimed atomically rather than checked and then set. Dispose is public
        // and IDisposable promises it is safe to call more than once; two
        // threads racing the check would each record the workflow, doubling it
        // in telemetry and disposing the Activity twice.
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        Ambient.Value = Parent;

        var tag = new KeyValuePair<string, object?>("workflow", Name);

        AgentMetrics.WorkflowOperations.Record(Operations, tag);
        AgentMetrics.WorkflowRequests.Record(Requests, tag);
        AgentMetrics.WorkflowDuration.Record(Elapsed.TotalSeconds, tag);

        var tokens = Tokens;

        // Recorded only when something reported. A histogram that takes a zero
        // for every unmeasured workflow has a p50 of zero and says nothing.
        if (UsageReported)
        {
            AgentMetrics.WorkflowInputTokens.Record(tokens.Input, tag);
            AgentMetrics.WorkflowOutputTokens.Record(tokens.Output, tag);
        }

        _activity?.SetTag("agentinfer.workflow.operations", Operations);
        _activity?.SetTag("agentinfer.workflow.requests", Requests);
        _activity?.SetTag("agentinfer.workflow.tool_calls", ToolCalls);

        if (UsageReported)
        {
            _activity?.SetTag("agentinfer.workflow.tokens.input", tokens.Input);
            _activity?.SetTag("agentinfer.workflow.tokens.output", tokens.Output);
            _activity?.SetTag("agentinfer.workflow.tokens.total", tokens.Total);
        }
        _activity?.Dispose();
    }

    /// <summary>`6 operations, 9 requests, 14 tool calls, 8.2k in / 1.1k out in 12.4s`.</summary>
    /// <remarks>
    /// The token half is left off entirely when no provider reported, rather
    /// than printed as zero. Somebody reading this line to decide whether a
    /// workflow is affordable should be able to tell "cheap" from "unmeasured".
    /// </remarks>
    public override string ToString()
    {
        var counted = $"{Operations} operation(s), {Requests} request(s), {ToolCalls} tool call(s)";
        var spent = UsageReported ? $", {Tokens.Input} in / {Tokens.Output} out token(s)" : string.Empty;

        return $"{Name}: {counted}{spent} in {Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>
    /// Adds one request's usage to this scope and every scope enclosing it.
    /// </summary>
    /// <remarks>
    /// Rolled up like <see cref="RecordOperation"/> and for the same reason: an
    /// agent reached as another agent's tool must not be able to spend a parent
    /// workflow's tokens without the parent seeing them.
    /// <para>
    /// Unlike <see cref="RecordRequest"/> this enforces no bound. It is
    /// bookkeeping after the fact — the tokens are already spent by the time a
    /// provider reports them, so refusing here would cost the money and throw
    /// the result away.
    /// </para>
    /// </remarks>
    internal void AddUsage(UsageDetails usage)
    {
        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;

        for (AgentScope? scope = this; scope is not null; scope = scope.Parent)
        {
            Interlocked.Add(ref scope._inputTokens, input);
            Interlocked.Add(ref scope._outputTokens, output);
            Interlocked.Add(ref scope._totalTokens, usage.TotalTokenCount ?? input + output);
            Interlocked.Add(ref scope._reasoningTokens, usage.ReasoningTokenCount ?? 0);
            Interlocked.Add(ref scope._cachedInputTokens, usage.CachedInputTokenCount ?? 0);
            Interlocked.Increment(ref scope._usageReports);
        }
    }

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
