using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Tests.Time;

public class RationalTests
{
    [Fact]
    public void NormalizesToLowestTermsWithPositiveDenominator()
    {
        var value = new Rational(60000, -2002);

        Assert.Equal(-30000, value.Numerator);
        Assert.Equal(1001, value.Denominator);
        Assert.Equal(new Rational(-30000, 1001), value);
    }

    [Fact]
    public void DefaultIsZero()
    {
        Assert.Equal(Rational.Zero, default);
        Assert.Equal(1, default(Rational).Denominator);
    }

    [Fact]
    public void ZeroDenominatorIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(1, 0));
    }

    [Fact]
    public void ArithmeticIsExact()
    {
        var a = new Rational(1, 3);
        var b = new Rational(1, 6);

        Assert.Equal(new Rational(1, 2), a + b);
        Assert.Equal(new Rational(1, 6), a - b);
        Assert.Equal(new Rational(1, 18), a * b);
        Assert.Equal(new Rational(2, 1), a / b);
        Assert.Equal(new Rational(1001, 30000), Rational.Fps29_97.Reciprocal());
    }

    [Fact]
    public void DivisionByZeroThrows()
    {
        Assert.Throws<DivideByZeroException>(() => Rational.One / Rational.Zero);
        Assert.Throws<DivideByZeroException>(() => Rational.Zero.Reciprocal());
    }

    [Fact]
    public void ComparesByValue()
    {
        Assert.True(Rational.Fps29_97 < 30);
        Assert.True(Rational.Fps59_94 > Rational.Fps29_97);
        Assert.Equal(0, new Rational(2, 4).CompareTo(new Rational(1, 2)));
    }

    [Theory]
    [InlineData(7, 2, Rounding.Down, 3)]
    [InlineData(7, 2, Rounding.Up, 4)]
    [InlineData(7, 2, Rounding.Nearest, 4)]
    [InlineData(-7, 2, Rounding.Down, -4)]
    [InlineData(-7, 2, Rounding.Up, -3)]
    [InlineData(-7, 2, Rounding.Nearest, -3)]
    [InlineData(5, 3, Rounding.Nearest, 2)]
    [InlineData(4, 3, Rounding.Nearest, 1)]
    [InlineData(6, 3, Rounding.Up, 2)]
    public void RoundsToInteger(long numerator, long denominator, Rounding rounding, long expected)
    {
        Assert.Equal(expected, new Rational(numerator, denominator).ToInt64(rounding));
    }

    [Fact]
    public void RescalesBetweenTimeBases()
    {
        // 90kHz のタイムスタンプ 3003 は 29.97fps のちょうど 1 フレーム
        var ninetyKHz = new Rational(1, 90000);
        var frameDuration = Rational.Fps29_97.Reciprocal();

        Assert.Equal(1, Rational.Rescale(3003, ninetyKHz, frameDuration, Rounding.Down));
        Assert.Equal(0, Rational.Rescale(3002, ninetyKHz, frameDuration, Rounding.Down));
        Assert.Equal(1, Rational.Rescale(3002, ninetyKHz, frameDuration, Rounding.Nearest));
        Assert.Equal(-1, Rational.Rescale(-1, ninetyKHz, frameDuration, Rounding.Down));
    }

    [Theory]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("30", 30, 1)]
    [InlineData("-48000/2", -24000, 1)]
    public void ParsesAndFormats(string text, long numerator, long denominator)
    {
        var value = Rational.Parse(text);

        Assert.Equal(new Rational(numerator, denominator), value);
        Assert.Equal(value, Rational.Parse(value.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1/0")]
    [InlineData("1/2/3")]
    [InlineData("29.97")]
    public void RejectsInvalidText(string text)
    {
        Assert.False(Rational.TryParse(text, out _));
    }
}
