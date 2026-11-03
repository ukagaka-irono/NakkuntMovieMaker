using NakkuntMovieMaker.Core.Animation;

namespace NakkuntMovieMaker.Core.Tests.Animation;

public class EasingTests
{
    public static TheoryData<Easing> AllEasings()
    {
        var data = new TheoryData<Easing> { Easing.Linear, Easing.Hold };
        foreach (var curve in Enum.GetValues<EasingCurve>().Where(curve => curve is not (EasingCurve.Linear or EasingCurve.Hold)))
        {
            foreach (var mode in Enum.GetValues<EasingMode>())
            {
                data.Add(new Easing(curve, mode));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllEasings))]
    public void StartsAtZeroAndEndsAtOne(Easing easing)
    {
        Assert.Equal(0, easing.Evaluate(0), 9);
        Assert.Equal(1, easing.Evaluate(1), 9);
    }

    [Theory]
    [MemberData(nameof(AllEasings))]
    public void ClampsProgressOutsideZeroToOne(Easing easing)
    {
        Assert.Equal(easing.Evaluate(0), easing.Evaluate(-0.5));
        Assert.Equal(easing.Evaluate(1), easing.Evaluate(1.5));
    }

    [Theory]
    [MemberData(nameof(AllEasings))]
    public void NameRoundTrips(Easing easing)
    {
        Assert.Equal(easing, Easing.Parse(easing.ToString()));
    }

    [Theory]
    [InlineData(EasingCurve.Sine)]
    [InlineData(EasingCurve.Quad)]
    [InlineData(EasingCurve.Cubic)]
    [InlineData(EasingCurve.Quart)]
    [InlineData(EasingCurve.Quint)]
    [InlineData(EasingCurve.Expo)]
    [InlineData(EasingCurve.Circ)]
    public void BasicCurvesIncreaseMonotonically(EasingCurve curve)
    {
        foreach (var mode in Enum.GetValues<EasingMode>())
        {
            var easing = new Easing(curve, mode);
            var previous = easing.Evaluate(0);
            for (var i = 1; i <= 1000; i++)
            {
                var current = easing.Evaluate(i / 1000.0);
                Assert.True(current >= previous - 1e-12, $"{easing} が {i / 1000.0} で減少しています");
                previous = current;
            }
        }
    }

    [Theory]
    [InlineData(EasingCurve.Quad)]
    [InlineData(EasingCurve.Cubic)]
    [InlineData(EasingCurve.Sine)]
    public void InIsSlowAtStartAndOutIsFastAtStart(EasingCurve curve)
    {
        Assert.True(new Easing(curve, EasingMode.In).Evaluate(0.25) < 0.25);
        Assert.True(new Easing(curve, EasingMode.Out).Evaluate(0.25) > 0.25);
        Assert.Equal(0.5, new Easing(curve, EasingMode.InOut).Evaluate(0.5), 9);
    }

    [Fact]
    public void BackOvershoots()
    {
        Assert.True(new Easing(EasingCurve.Back, EasingMode.In).Evaluate(0.2) < 0);
        Assert.True(new Easing(EasingCurve.Back, EasingMode.Out).Evaluate(0.8) > 1);
    }

    [Fact]
    public void HoldKeepsTheStartValueUntilTheEnd()
    {
        Assert.Equal(0, Easing.Hold.Evaluate(0.999));
        Assert.Equal(1, Easing.Hold.Evaluate(1));
    }

    [Fact]
    public void LinearIgnoresMode()
    {
        Assert.Equal(default, Easing.Linear);
        Assert.Equal(Easing.Linear, new Easing(EasingCurve.Linear, EasingMode.Out));
        Assert.Equal("linear", default(Easing).ToString());
    }

    [Theory]
    [InlineData("cubic-in-out", EasingCurve.Cubic, EasingMode.InOut)]
    [InlineData("back-out", EasingCurve.Back, EasingMode.Out)]
    [InlineData("expo-in", EasingCurve.Expo, EasingMode.In)]
    public void ParsesNames(string text, EasingCurve curve, EasingMode mode)
    {
        Assert.Equal(new Easing(curve, mode), Easing.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cubic")]
    [InlineData("cubic-sideways")]
    [InlineData("linear-in")]
    [InlineData("Cubic-In")]
    public void RejectsUnknownNames(string text)
    {
        Assert.False(Easing.TryParse(text, out _));
    }
}
