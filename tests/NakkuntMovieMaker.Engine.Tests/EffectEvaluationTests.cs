using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Effects;

namespace NakkuntMovieMaker.Engine.Tests;

public class EffectEvaluationTests
{
    private static readonly Scene Empty = Scene.CreateNew("テスト", SceneFormat.Landscape1080p30, 1);

    private static EffectInstance Effect(EffectDefinition definition, params (string Name, ParameterValue Value)[] values) =>
        new(EffectId.New(), definition.Type, values.ToImmutableDictionary(value => value.Name, value => value.Value));

    private static ImageItem ImageWith(long length, params EffectInstance[] effects) =>
        new(ItemId.New(), Empty.Layers[0].Id, 0, length, AssetId.New()) { Effects = [.. effects] };

    private static EvaluatedEffect[] Evaluate(long frame, long length, params EffectInstance[] effects) =>
        [.. TimelineEvaluator.EvaluateEffects(effects, new EffectTime(frame, length, 30), EffectRegistry.Default)];

    [Fact]
    public void FillsMissingParametersWithDefaults()
    {
        var evaluated = Assert.Single(Evaluate(0, 30, Effect(BuiltInEffects.Blur)));

        Assert.Equal("nmm.blur", evaluated.Type);
        Assert.Equal(10, evaluated.Parameters.GetNumber("radius"));
    }

    [Fact]
    public void EvaluatesKeyframesAndClampsToTheRange()
    {
        var radius = Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(10, 20));
        var blur = Effect(BuiltInEffects.Blur, ("radius", new NumberParameter(radius)));
        var adjust = Effect(BuiltInEffects.ColorAdjust, ("brightness", new NumberParameter(250.0)));

        var evaluated = Evaluate(5, 30, blur, adjust);

        Assert.Equal(10, evaluated[0].Parameters.GetNumber("radius"));
        Assert.Equal(100, evaluated[1].Parameters.GetNumber("brightness")); // 範囲は -100〜100
    }

    [Fact]
    public void SkipsDisabledAndUnknownEffects()
    {
        var disabled = Effect(BuiltInEffects.Blur) with { Enabled = false };
        var unknown = new EffectInstance(EffectId.New(), "plugin.unknown", ImmutableDictionary<string, ParameterValue>.Empty);
        var mono = Effect(BuiltInEffects.Monochrome);

        Assert.Equal(["nmm.monochrome"], Evaluate(0, 30, disabled, unknown, mono).Select(effect => effect.Type));
    }

    [Fact]
    public void UsesDefaultsForMismatchedOrInvalidValues()
    {
        var mask = Effect(
            BuiltInEffects.Mask,
            ("width", new TextParameter("太い")),
            ("shape", new TextParameter("hexagon")),
            ("invert", new BoolParameter(true)));

        var parameters = Assert.Single(Evaluate(0, 30, mask)).Parameters;

        Assert.Equal(200, parameters.GetNumber("width"));
        Assert.Equal("rectangle", parameters.GetText("shape"));
        Assert.True(parameters.GetBool("invert"));
    }

    [Theory]
    [InlineData(0, 0.0)] // 最初のフレームは透明
    [InlineData(15, 0.5)] // フェードイン 1 秒の半分
    [InlineData(45, 1.0)]
    [InlineData(74, 0.5)] // 終わりまで 15 フレーム
    [InlineData(89, 0.0)] // 最後のフレームは透明
    public void FadeComputesTheAlphaFromTheTime(long frame, double alpha)
    {
        var fade = Effect(BuiltInEffects.Fade, ("fadeIn", new NumberParameter(1.0)), ("fadeOut", new NumberParameter(1.0)));

        Assert.Equal(alpha, Assert.Single(Evaluate(frame, 90, fade)).Parameters.GetNumber("alpha"), 9);
    }

    [Fact]
    public void NoiseChangesEveryFrameOnlyWhenAnimated()
    {
        var animated = Effect(BuiltInEffects.Noise);
        var still = Effect(BuiltInEffects.Noise, ("animated", new BoolParameter(false)));

        Assert.Equal(7, Assert.Single(Evaluate(7, 30, animated)).Parameters.GetNumber("frame"));
        Assert.Equal(0, Assert.Single(Evaluate(7, 30, still)).Parameters.GetNumber("frame"));
    }

    [Fact]
    public void ShakeIsDeterministicAndHoldsBetweenSteps()
    {
        // 1 秒に 10 回：3 フレームごとに変わる
        var shake = Effect(
            BuiltInEffects.Shake,
            ("amplitudeX", new NumberParameter(20.0)),
            ("amplitudeY", new NumberParameter(5.0)),
            ("frequency", new NumberParameter(10.0)));

        var first = Assert.Single(Evaluate(3, 30, shake)).Parameters;
        Assert.Equal(first, Assert.Single(Evaluate(3, 30, shake)).Parameters);
        Assert.Equal(first, Assert.Single(Evaluate(5, 30, shake)).Parameters);
        Assert.NotEqual(first, Assert.Single(Evaluate(6, 30, shake)).Parameters);

        var offsets = Enumerable.Range(0, 30).Select(frame => Assert.Single(Evaluate(frame, 30, shake)).Parameters).ToList();
        Assert.All(offsets, p => Assert.InRange(p.GetNumber("offsetX"), -20, 20));
        Assert.All(offsets, p => Assert.InRange(p.GetNumber("offsetY"), -5, 5));
        Assert.Equal(0, offsets[0].GetNumber("angle"));
    }

    [Fact]
    public void PlansStayEqualWhileEffectsDoNotChange()
    {
        var blurred = ImageWith(60, Effect(BuiltInEffects.Blur));
        var scene = Empty with { Items = Empty.Items.Add(blurred.Id, blurred) };
        Assert.Equal(TimelineEvaluator.Evaluate(scene, 3), TimelineEvaluator.Evaluate(scene, 40));

        var noisy = ImageWith(60, Effect(BuiltInEffects.Noise));
        var noisyScene = Empty with { Items = Empty.Items.Add(noisy.Id, noisy) };
        Assert.NotEqual(TimelineEvaluator.Evaluate(noisyScene, 3), TimelineEvaluator.Evaluate(noisyScene, 4));

        var node = Assert.IsType<DrawNode>(Assert.Single(TimelineEvaluator.Evaluate(scene, 0).Nodes));
        Assert.Equal("nmm.blur", Assert.Single(node.Effects).Type);
    }

    [Fact]
    public void EveryBuiltInEffectHasUniqueTypeAndParameterNames()
    {
        Assert.Equal(15, BuiltInEffects.All.Length);
        Assert.Equal(BuiltInEffects.All.Length, BuiltInEffects.All.Select(effect => effect.Type).Distinct().Count());
        Assert.All(BuiltInEffects.All, effect =>
        {
            Assert.StartsWith("nmm.", effect.Type);
            Assert.Equal(effect.Parameters.Length, effect.Parameters.Select(parameter => parameter.Name).Distinct().Count());
            Assert.All(effect.Parameters, parameter => Assert.NotNull(parameter.Default));
        });
    }
}
