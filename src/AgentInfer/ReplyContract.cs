using System.Text.Json.Serialization.Metadata;

namespace AgentInfer;

/// <summary>
/// Everything derived from one return type, in one object.
/// </summary>
/// <remarks>
/// <para>
/// Three things have to agree about a typed reply: the schema the model is
/// told, the metadata the reply is bound with, and the rule its values must
/// satisfy. They were in three different places — a string on
/// <see cref="AgentCall"/>, a <c>JsonSerializerOptions</c>, and reflection
/// inside the runner — and nothing kept them in step. This repo has paid for
/// that twice: a model answered <c>{"supported": true}</c> because the schema
/// was missing, then <c>score: 100</c> because the schema and the validator
/// disagreed about what an integer meant.
/// </para>
/// <para>
/// Put them in one object and they are either generated together from one read
/// of the type, or written together by hand. Either way they cannot drift
/// apart independently, which is the only structural fix — the alternative is
/// three places to remember.
/// </para>
/// <para>
/// <b>It is also what makes the typed path trimmable.</b>
/// <see cref="JsonTypeInfo{T}"/> comes from <c>System.Text.Json</c>'s own
/// source generator, so binding through it needs no reflection and carries no
/// <c>[RequiresUnreferencedCode]</c>. Validation moves out of DataAnnotations,
/// which is reflective, has no source-generated equivalent, and — worse —
/// fails <em>open</em> under trimming: if the property metadata is gone it
/// finds nothing to check and reports success.
/// </para>
/// <para>
/// Supplied by the caller rather than built here, for the same reason the
/// library does not construct an <c>IChatClient</c>: at the time this assembly
/// is compiled, <typeparamref name="T"/> is whatever record somebody has not
/// written yet. Only the consumer's compilation knows.
/// </para>
/// </remarks>
public interface IReplyContract<T>
{
    /// <summary>JSON Schema for what the model must produce.</summary>
    public string Schema { get; }

    /// <summary>How the reply is bound. From a <c>JsonSerializerContext</c>.</summary>
    public JsonTypeInfo<T> TypeInfo { get; }

    /// <summary>
    /// What is wrong with this value, or <c>null</c> when nothing is.
    /// </summary>
    /// <remarks>
    /// A string rather than a thrown exception, because the contract knows the
    /// rule and the runner knows the context — the operation that was called
    /// and what the model actually said, which is the half of the message worth
    /// reading.
    /// </remarks>
    public string? Validate(T value);
}
