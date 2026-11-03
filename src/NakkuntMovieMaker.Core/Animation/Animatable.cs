using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Animation;

/// <summary>
/// キーフレーム。Frame はアイテムの先頭からの相対フレーム。
/// Easing は、このキーフレームから次のキーフレームまでの変化のしかた
/// </summary>
public readonly record struct Keyframe<T>(long Frame, T Value, Easing Easing = default);

/// <summary>
/// キーフレームでアニメーションできる値。キーフレームがなければ Value のまま変わらない。
/// キーフレームは Frame の昇順で、同じ Frame は重複しないことを保証する
/// </summary>
public sealed record Animatable<T>
{
    private Animatable(T value, EquatableArray<Keyframe<T>> keys)
    {
        Value = value;
        Keys = keys;
    }

    /// <summary>キーフレームがないときの値</summary>
    public T Value { get; }

    public EquatableArray<Keyframe<T>> Keys { get; }

    public bool IsAnimated => !Keys.IsEmpty;

    public static implicit operator Animatable<T>(T value) => Constant(value);

    public static Animatable<T> Constant(T value) => new(value, EquatableArray<Keyframe<T>>.Empty);

    /// <summary>キーフレームから作る。Value には最初のキーフレームの値を入れる</summary>
    /// <exception cref="ArgumentException">キーフレームがない、または同じ Frame が重複している</exception>
    public static Animatable<T> FromKeys(IEnumerable<Keyframe<T>> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var sorted = keys.OrderBy(key => key.Frame).ToEquatableArray();
        if (sorted.IsEmpty)
        {
            throw new ArgumentException("キーフレームが 1 つもありません", nameof(keys));
        }

        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Frame == sorted[i - 1].Frame)
            {
                throw new ArgumentException($"同じフレームにキーフレームが重複しています: {sorted[i].Frame}", nameof(keys));
            }
        }

        return new Animatable<T>(sorted[0].Value, sorted);
    }

    /// <summary>キーフレームを追加する。同じフレームにあれば置き換える</summary>
    public Animatable<T> SetKey(Keyframe<T> key) =>
        FromKeys(Keys.Where(existing => existing.Frame != key.Frame).Append(key));

    /// <summary>指定したフレームのキーフレームを削除する。最後の 1 つを消したら、その値の定数になる</summary>
    public Animatable<T> RemoveKey(long frame)
    {
        var remaining = Keys.Where(key => key.Frame != frame).ToList();
        if (remaining.Count == Keys.Length)
        {
            return this;
        }

        return remaining.Count == 0 ? Constant(Keys[0].Value) : FromKeys(remaining);
    }

    /// <summary>アイテムの先頭からの相対フレームでの値。小数のフレーム（モーションブラーなど）も扱える</summary>
    public T Evaluate(double frame)
    {
        if (Keys.IsEmpty)
        {
            return Value;
        }

        var keys = Keys.AsImmutableArray();
        if (frame <= keys[0].Frame)
        {
            return keys[0].Value;
        }

        if (frame >= keys[^1].Frame)
        {
            return keys[^1].Value;
        }

        // frame を挟む 2 つのキーフレームを二分探索で探す
        var low = 0;
        var high = keys.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (keys[middle].Frame <= frame)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        var from = keys[low];
        var to = keys[high];
        var progress = (frame - from.Frame) / (to.Frame - from.Frame);
        return Interpolation<T>.Lerp(from.Value, to.Value, from.Easing.Evaluate(progress));
    }
}

/// <summary>型推論のための入口</summary>
public static class Animatable
{
    public static Animatable<T> Constant<T>(T value) => Animatable<T>.Constant(value);

    public static Animatable<T> FromKeys<T>(params IEnumerable<Keyframe<T>> keys) => Animatable<T>.FromKeys(keys);
}

/// <summary>型ごとの線形補間。アニメーションできる型はここに追加する</summary>
internal static class Interpolation<T>
{
    public static readonly Func<T, T, double, T> Lerp = Create();

    private static Func<T, T, double, T> Create()
    {
        if (typeof(T) == typeof(double))
        {
            return (Func<T, T, double, T>)(object)(Func<double, double, double, double>)((a, b, t) => a + (b - a) * t);
        }

        if (typeof(T) == typeof(RgbaColor))
        {
            return (Func<T, T, double, T>)(object)(Func<RgbaColor, RgbaColor, double, RgbaColor>)LerpColor;
        }

        // 補間できない型は、次のキーフレームに着いた時点で切り替わる
        return (a, b, t) => t >= 1 ? b : a;
    }

    private static RgbaColor LerpColor(RgbaColor a, RgbaColor b, double t) =>
        new(LerpByte(a.R, b.R, t), LerpByte(a.G, b.G, t), LerpByte(a.B, b.B, t), LerpByte(a.A, b.A, t));

    // Back や Elastic で範囲をはみ出しても、0〜255 に収める
    private static byte LerpByte(byte a, byte b, double t) =>
        (byte)Math.Clamp(Math.Round(a + (b - a) * t, MidpointRounding.AwayFromZero), 0, 255);
}
