using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Tests.Animation;

public class AnimatableTests
{
    [Fact]
    public void ConstantReturnsItsValueEverywhere()
    {
        Animatable<double> value = 42.0;

        Assert.False(value.IsAnimated);
        Assert.Equal(42, value.Evaluate(-100));
        Assert.Equal(42, value.Evaluate(0));
        Assert.Equal(42, value.Evaluate(1e9));
    }

    [Fact]
    public void InterpolatesLinearlyBetweenKeys()
    {
        var value = Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(10, 100));

        Assert.Equal(0, value.Evaluate(0));
        Assert.Equal(25, value.Evaluate(2.5), 9);
        Assert.Equal(50, value.Evaluate(5));
        Assert.Equal(100, value.Evaluate(10));
    }

    [Fact]
    public void HoldsFirstAndLastValuesOutsideKeys()
    {
        var value = Animatable.FromKeys(new Keyframe<double>(10, 1), new Keyframe<double>(20, 2));

        Assert.Equal(1, value.Evaluate(0));
        Assert.Equal(2, value.Evaluate(30));
    }

    [Fact]
    public void UsesEasingOfTheStartingKey()
    {
        var value = Animatable.FromKeys(
            new Keyframe<double>(0, 0, new Easing(EasingCurve.Quad, EasingMode.In)),
            new Keyframe<double>(10, 100),
            new Keyframe<double>(20, 0, Easing.Hold),
            new Keyframe<double>(30, 50));

        Assert.Equal(25, value.Evaluate(5), 9); // 0.5² = 0.25
        Assert.Equal(50, value.Evaluate(15), 9); // 2 つ目のキーは既定の Linear
        Assert.Equal(0, value.Evaluate(29.9)); // Hold は次のキーまで値を保つ
        Assert.Equal(50, value.Evaluate(30));
    }

    [Fact]
    public void FindsTheRightSegmentAmongManyKeys()
    {
        var keys = Enumerable.Range(0, 1000).Select(i => new Keyframe<double>(i * 10, i));
        var value = Animatable.FromKeys(keys);

        Assert.Equal(123.5, value.Evaluate(1235), 9);
        Assert.Equal(999, value.Evaluate(99990));
    }

    [Fact]
    public void SortsKeysAndRejectsDuplicates()
    {
        var value = Animatable.FromKeys(new Keyframe<double>(10, 1), new Keyframe<double>(0, 0));

        Assert.Equal([0L, 10L], value.Keys.Select(key => key.Frame));
        Assert.Throws<ArgumentException>(() =>
            Animatable.FromKeys(new Keyframe<double>(5, 1), new Keyframe<double>(5, 2)));
        Assert.Throws<ArgumentException>(() => Animatable.FromKeys<double>());
    }

    [Fact]
    public void SetKeyAddsOrReplaces()
    {
        Animatable<double> value = 0.0;

        var animated = value.SetKey(new Keyframe<double>(0, 1)).SetKey(new Keyframe<double>(10, 2)).SetKey(new Keyframe<double>(0, 5));

        Assert.Equal(2, animated.Keys.Length);
        Assert.Equal(5, animated.Evaluate(0));
    }

    [Fact]
    public void RemovingTheLastKeyLeavesAConstant()
    {
        var value = Animatable.FromKeys(new Keyframe<double>(0, 7));

        var removed = value.RemoveKey(0);

        Assert.False(removed.IsAnimated);
        Assert.Equal(7, removed.Evaluate(0));
        Assert.Same(value, value.RemoveKey(99));
    }

    [Fact]
    public void ComparesByValue()
    {
        var a = Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(10, 1));
        var b = Animatable.FromKeys(new Keyframe<double>(10, 1), new Keyframe<double>(0, 0));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a.SetKey(new Keyframe<double>(5, 3)));
        Assert.Equal(Animatable.Constant(1.0), (Animatable<double>)1.0);
    }

    [Fact]
    public void InterpolatesColorsAndClampsOvershoot()
    {
        var value = Animatable.FromKeys(
            new Keyframe<RgbaColor>(0, RgbaColor.Black, new Easing(EasingCurve.Back, EasingMode.Out)),
            new Keyframe<RgbaColor>(10, RgbaColor.White));

        Assert.Equal(new RgbaColor(128, 128, 128), Animatable.FromKeys(
            new Keyframe<RgbaColor>(0, RgbaColor.Black),
            new Keyframe<RgbaColor>(10, RgbaColor.White)).Evaluate(5));
        Assert.Equal(RgbaColor.White, value.Evaluate(8)); // 255 を超えても 255 に収まる
    }

    [Fact]
    public void NonInterpolatableTypesSwitchAtTheNextKey()
    {
        var value = Animatable.FromKeys(new Keyframe<string>(0, "a"), new Keyframe<string>(10, "b"));

        Assert.Equal("a", value.Evaluate(9.9));
        Assert.Equal("b", value.Evaluate(10));
    }
}
