namespace AgentInfer;

/// <summary>
/// One try at a typed reply: the value, or what was wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// A model that answers <c>urgency: 9</c> against a declared 1–5 has not
/// malfunctioned — it has done something anticipated, which the caller is
/// expected to handle by asking again with the problem attached. That is not
/// an exceptional outcome, and the repair loop that treats it as one pays
/// three ways: a debugger reports a first-chance exception on every ordinary
/// run, a throw costs more than a return, and <c>try</c>/<c>catch</c> reads as
/// "something went wrong" where the code means "try again".
/// </para>
/// <para>
/// So the anticipated path returns this and the unanticipated one throws.
/// <see cref="AgentRunner.CompleteJsonAsync{T}(AgentCall, IReplyContract{T}, CancellationToken)"/>
/// keeps throwing, because most callers do not repair and forcing all of them
/// through a result type to serve the few who do would be a tax. It is
/// implemented by calling the Try version, so there is one binding path rather
/// than two that can disagree.
/// </para>
/// <para>
/// Only a reply that will not bind or will not validate arrives here. A refused
/// connection, a spent budget or a cancellation still throws — those are not
/// outcomes the model produced, and nothing is gained by making every caller
/// check for them.
/// </para>
/// </remarks>
public readonly struct ReplyAttempt<T>
{
    private readonly T _value;

    private ReplyAttempt(T value, string? problem)
    {
        _value = value;
        Problem = problem;
    }

    /// <summary>
    /// What was wrong with the reply, or <c>null</c> when nothing was.
    /// </summary>
    /// <remarks>
    /// The same sentence <see cref="AgentException"/> would have carried,
    /// quoting what the model actually said — because it is what the repair
    /// hands back to the model, and a shorter version would be a second message
    /// to keep in step with the first.
    /// </remarks>
    public string? Problem { get; }

    public bool Succeeded => Problem is null;

    /// <summary>The bound reply.</summary>
    /// <exception cref="InvalidOperationException">The attempt failed.</exception>
    public T Value => Succeeded
        ? _value
        : throw new InvalidOperationException(
            $"This attempt did not produce a {typeof(T).Name}: {Problem}");

    public static ReplyAttempt<T> Ok(T value) => new(value, null);

    public static ReplyAttempt<T> Failed(string problem) => new(default!, problem);

    /// <summary>Lets a caller take both halves in one line.</summary>
    public void Deconstruct(out bool succeeded, out T value, out string? problem)
    {
        succeeded = Succeeded;
        value = _value;
        problem = Problem;
    }
}
