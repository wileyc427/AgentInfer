using System.Text.Json.Serialization.Metadata;

namespace AgentInfer;

/// <summary>
/// Everything derived from one return type, in one object.
/// </summary>
/// <remarks>
/// <para>
/// Three things have to agree about a typed reply: the schema the model is
/// told, the metadata the reply is bound with, and the rule its values must
/// satisfy. Kept in separate places they drift, and a schema that disagrees
/// with its validator is worse than no schema — the model obeys it and the
/// bind fails anyway.
/// </para>
/// <para>
/// In one object they are either generated together from a single read of the
/// type or written together by hand, so they cannot drift independently.
/// </para>
/// <para>
/// It is also what makes the typed path trimmable.
/// <see cref="JsonTypeInfo{T}"/> comes from <c>System.Text.Json</c>'s own
/// source generator, so binding through it needs no reflection. Validation
/// moves off DataAnnotations, which is reflective and fails <em>open</em> under
/// trimming: with the property metadata gone it finds nothing to check and
/// reports success.
/// </para>
/// <para>
/// Supplied by the caller rather than built here: when this assembly is
/// compiled <typeparamref name="T"/> does not exist yet, so only the consumer's
/// compilation can produce it.
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
