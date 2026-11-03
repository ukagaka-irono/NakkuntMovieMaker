using System.Collections.Immutable;

namespace NakkuntMovieMaker.Core.Collections;

/// <summary>
/// 不変コレクションを中身で比較する。ImmutableList・ImmutableDictionary は参照で比較されるので、
/// モデルの record の Equals からこれを使う。同じインスタンスなら中身を見ずに true を返す
/// </summary>
public static class CollectionEquality
{
    public static bool ListEquals<T>(ImmutableList<T>? left, ImmutableList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        return left.SequenceEqual(right, EqualityComparer<T>.Default);
    }

    public static bool DictionaryEquals<TKey, TValue>(ImmutableDictionary<TKey, TValue>? left, ImmutableDictionary<TKey, TValue>? right)
        where TKey : notnull
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !EqualityComparer<TValue>.Default.Equals(value, other))
            {
                return false;
            }
        }

        return true;
    }
}
