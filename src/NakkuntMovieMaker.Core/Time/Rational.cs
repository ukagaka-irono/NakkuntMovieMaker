using System.Globalization;

namespace NakkuntMovieMaker.Core.Time;

/// <summary>端数の扱いを指定する</summary>
public enum Rounding
{
    /// <summary>小さいほうへ丸める（負の値も含めて床関数）</summary>
    Down,

    /// <summary>大きいほうへ丸める（天井関数）</summary>
    Up,

    /// <summary>最も近い整数へ丸める。ちょうど半分のときは大きいほうへ</summary>
    Nearest,
}

/// <summary>
/// 有理数。フレームレート（30000/1001 など）や FFmpeg のタイムベースを誤差なく扱うために使う。
/// 常に既約で、分母は正になるように正規化する
/// </summary>
public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    public static readonly Rational Zero = new(0, 1);
    public static readonly Rational One = new(1, 1);

    /// <summary>23.976fps（24000/1001）</summary>
    public static readonly Rational Fps23_976 = new(24000, 1001);

    /// <summary>29.97fps（30000/1001）</summary>
    public static readonly Rational Fps29_97 = new(30000, 1001);

    /// <summary>59.94fps（60000/1001）</summary>
    public static readonly Rational Fps59_94 = new(60000, 1001);

    private readonly long _denominator;

    public Rational(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "分母に 0 は指定できません");
        }

        if (denominator < 0)
        {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        var gcd = (long)Int128.Abs(Gcd(numerator, denominator));
        Numerator = numerator / gcd;
        _denominator = denominator / gcd;
    }

    public long Numerator { get; }

    // default(Rational) を 0/1 として扱えるように、0 のときは 1 を返す
    public long Denominator => _denominator == 0 ? 1 : _denominator;

    public bool IsZero => Numerator == 0;

    public static implicit operator Rational(long value) => new(value, 1);

    public static Rational operator -(Rational value) => new(checked(-value.Numerator), value.Denominator);

    public static Rational operator +(Rational left, Rational right) =>
        FromWide((Int128)left.Numerator * right.Denominator + (Int128)right.Numerator * left.Denominator,
            (Int128)left.Denominator * right.Denominator);

    public static Rational operator -(Rational left, Rational right) => left + -right;

    public static Rational operator *(Rational left, Rational right) =>
        FromWide((Int128)left.Numerator * right.Numerator, (Int128)left.Denominator * right.Denominator);

    public static Rational operator /(Rational left, Rational right)
    {
        if (right.IsZero)
        {
            throw new DivideByZeroException();
        }

        return FromWide((Int128)left.Numerator * right.Denominator, (Int128)left.Denominator * right.Numerator);
    }

    public static bool operator ==(Rational left, Rational right) => left.Equals(right);

    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);

    public static bool operator <(Rational left, Rational right) => left.CompareTo(right) < 0;

    public static bool operator >(Rational left, Rational right) => left.CompareTo(right) > 0;

    public static bool operator <=(Rational left, Rational right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Rational left, Rational right) => left.CompareTo(right) >= 0;

    /// <summary>逆数</summary>
    public Rational Reciprocal()
    {
        if (IsZero)
        {
            throw new DivideByZeroException();
        }

        return new Rational(Denominator, Numerator);
    }

    /// <summary>指定した丸め方で整数にする</summary>
    public long ToInt64(Rounding rounding) => DivideRounded(Numerator, Denominator, rounding);

    public double ToDouble() => (double)Numerator / Denominator;

    /// <summary>value × from を to の単位に変換する（FFmpeg の av_rescale_q_rnd と同じ考え方）</summary>
    public static long Rescale(long value, Rational from, Rational to, Rounding rounding) =>
        DivideRounded((Int128)value * from.Numerator * to.Denominator, (Int128)from.Denominator * to.Numerator, rounding);

    /// <summary>整数どうしの割り算を、指定した丸め方で行う。分母は正であること</summary>
    internal static long DivideRounded(Int128 numerator, Int128 denominator, Rounding rounding)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "分母は正の数にしてください");
        }

        var quotient = Int128.DivRem(numerator, denominator);
        var floor = quotient.Remainder < 0 ? quotient.Quotient - 1 : quotient.Quotient;
        var remainder = numerator - floor * denominator; // 0 <= remainder < denominator

        var result = rounding switch
        {
            Rounding.Down => floor,
            Rounding.Up => remainder == 0 ? floor : floor + 1,
            Rounding.Nearest => remainder * 2 >= denominator ? floor + 1 : floor,
            _ => throw new ArgumentOutOfRangeException(nameof(rounding)),
        };
        return checked((long)result);
    }

    public bool Equals(Rational other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Rational other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public int CompareTo(Rational other) =>
        ((Int128)Numerator * other.Denominator).CompareTo((Int128)other.Numerator * Denominator);

    /// <summary>"30000/1001" の形。分母が 1 のときは "30"</summary>
    public override string ToString() =>
        Denominator == 1
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"30000/1001" または "30" の形を読む</summary>
    public static Rational Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryParse(text, out var value))
        {
            throw new FormatException($"有理数として読めません: {text}");
        }

        return value;
    }

    public static bool TryParse(string? text, out Rational value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var numeratorText = slash < 0 ? text : text[..slash];
        var denominatorText = slash < 0 ? "1" : text[(slash + 1)..];

        if (!long.TryParse(numeratorText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var numerator)
            || !long.TryParse(denominatorText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0)
        {
            return false;
        }

        value = new Rational(numerator, denominator);
        return true;
    }

    private static Rational FromWide(Int128 numerator, Int128 denominator)
    {
        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = Int128.Abs(Gcd(numerator, denominator));
        if (gcd == 0)
        {
            gcd = 1;
        }

        return new Rational(checked((long)(numerator / gcd)), checked((long)(denominator / gcd)));
    }

    private static Int128 Gcd(Int128 a, Int128 b)
    {
        a = Int128.Abs(a);
        b = Int128.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }
}
