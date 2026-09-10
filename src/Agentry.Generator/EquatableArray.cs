using System.Collections;
using System.Collections.Immutable;

namespace Agentry.Generator;

/// <summary>
/// An array that compares by value, for use inside pipeline models.
/// </summary>
/// <remarks>
/// <para>
/// This tiny type is load-bearing and its absence is the most common source of
/// slow generators. Roslyn caches each pipeline step by comparing the previous
/// model to the new one; if they are equal it skips everything downstream. A
/// plain array or <see cref="ImmutableArray{T}"/> compares by REFERENCE, so a
/// model containing one is never equal to a freshly built model, the cache
/// never hits, and the generator re-emits everything on every keystroke in the
/// IDE.
/// </para>
/// <para>
/// The failure is silent — correct output, terrible editor. Wrap every
/// collection that goes into a model.
/// </para>
/// </remarks>
internal readonly struct EquatableArray<T>(ImmutableArray<T> values)
    : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _values = values;

    public int Count => _values.IsDefault ? 0 : _values.Length;

    public bool Equals(EquatableArray<T> other)
    {
        if (_values.IsDefault || other._values.IsDefault) return _values.IsDefault && other._values.IsDefault;
        if (_values.Length != other._values.Length) return false;

        for (var i = 0; i < _values.Length; i++)
        {
            if (!_values[i].Equals(other._values[i])) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_values.IsDefault) return 0;

        var hash = 17;
        foreach (var value in _values) hash = (hash * 31) + value.GetHashCode();
        return hash;
    }

    public IEnumerator<T> GetEnumerator() =>
        (_values.IsDefault ? ImmutableArray<T>.Empty : _values).AsEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
