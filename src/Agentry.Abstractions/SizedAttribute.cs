using System;

namespace Agentry;

/// <summary>
/// How long a string may be, or how many items a collection may hold.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="BoundedAttribute"/> for things with a length,
/// and there for the same reason: <c>[MinLength]</c> and <c>[MaxLength]</c>
/// carry <c>[RequiresUnreferencedCode]</c> on their constructors, so writing
/// one makes the assembly that holds the record unverifiable under trimming.
/// </para>
/// <para>
/// Named rather than positional, because one of the two is usually absent and
/// <c>[Sized(0, 400)]</c> does not say which end it is bounding. The defaults
/// are the vacuous bounds, so an unset end emits no schema keyword and no
/// check rather than a condition that is always false.
/// </para>
/// <para>
/// It measures characters on a string and items on anything else — the same
/// split the schema makes between <c>maxLength</c> and <c>maxItems</c>, so the
/// bound the model is told and the bound it is held to are about the same
/// thing.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class SizedAttribute : Attribute
{
    /// <summary>The fewest characters or items. Zero means unbounded below.</summary>
    public int Min { get; set; }

    /// <summary>The most characters or items. <see cref="int.MaxValue"/> means unbounded above.</summary>
    public int Max { get; set; } = int.MaxValue;
}
