using System;

namespace Agentry;

/// <summary>
/// The range a value must fall in. Told to the model, and checked after.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists next to <c>[Range]</c>.</b> DataAnnotations is what a
/// .NET developer reaches for, and this library read it for exactly that
/// reason. Then the trim analyzer was switched on and
/// <c>MaxLengthAttribute</c>'s own constructor turned out to be
/// <c>[RequiresUnreferencedCode]</c> — because <c>ValidationAttribute.IsValid</c>
/// inspects arbitrary types reflectively. So a DataAnnotation is trim-hostile
/// <em>where it is written</em>, not only where it is enforced: putting
/// <c>[Range(1, 5)]</c> on a record makes that record's assembly
/// unverifiable, even when nothing ever reflects over it.
/// </para>
/// <para>
/// This attribute has no <c>IsValid</c>, no base class and no behaviour. It is
/// a fact the generator reads at compile time, and it compiles to metadata
/// nothing needs to inspect at run time. <c>[Range]</c> still works and is
/// still read — it is the right choice when the assembly is not a trimming
/// target, which is most of them.
/// </para>
/// <para>
/// Two constructors rather than one taking <c>double</c>, because the bound is
/// emitted into a C# pattern as well as into a schema, and
/// <c>value.Score is &lt; 1.0</c> does not compile against an <c>int</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class BoundedAttribute : Attribute
{
    public BoundedAttribute(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    public BoundedAttribute(double minimum, double maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>The smallest acceptable value, inclusive.</summary>
    public object Minimum { get; }

    /// <summary>The largest acceptable value, inclusive.</summary>
    public object Maximum { get; }
}
