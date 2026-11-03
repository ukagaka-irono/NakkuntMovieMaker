using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace NakkuntMovieMaker.Core.Collections;

/// <summary>
/// 中身で比較できる不変の配列。ImmutableArray は参照で比較されるので、
/// record のメンバーとして値の比較をしたい小さな配列（キーフレームなど）に使う
/// </summary>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items)
    {
        _items = items.IsDefault ? ImmutableArray<T>.Empty : items;
    }

    public ImmutableArray<T> AsImmutableArray() => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Length => AsImmutableArray().Length;

    public bool IsEmpty => Length == 0;

    int IReadOnlyCollection<T>.Count => Length;

    public T this[int index] => AsImmutableArray()[index];

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public bool Equals(EquatableArray<T> other) =>
        AsImmutableArray().AsSpan().SequenceEqual(other.AsImmutableArray().AsSpan(), EqualityComparer<T>.Default);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in AsImmutableArray())
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)AsImmutableArray()).GetEnumerator();
}

public static class EquatableArray
{
    public static EquatableArray<T> Create<T>(ReadOnlySpan<T> items) => new([.. items]);

    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items) => new([.. items]);
}
