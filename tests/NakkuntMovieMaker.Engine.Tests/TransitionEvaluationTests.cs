using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Tests;

public class TransitionEvaluationTests
{
    // 30fps、レイヤー 2 つ
    private static readonly Scene Empty = Scene.CreateNew("テスト", SceneFormat.Landscape1080p30, 2);

    private static ImageItem Image(long start, long length, TransitionInstance? transition = null, int layer = 0) =>
        new(ItemId.New(), Empty.Layers[layer].Id, start, length, AssetId.New()) { Transition = transition };

    private static Scene With(params Item[] items) =>
        Empty with { Items = Empty.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };

    private static TransitionInstance Crossfade(long duration) => new("nmm.crossfade", duration);

    private static RenderNode Single(Scene scene, long frame) => Assert.Single(TimelineEvaluator.Evaluate(scene, frame).Nodes);

    [Fact]
    public void CentersTheTransitionOnTheCut()
    {
        // A [0, 30)・B [30, 60)、10 フレームのクロスフェードは [25, 35)
        var a = Image(0, 30);
        var b = Image(30, 30, Crossfade(10));
        var scene = With(a, b);

        Assert.Equal(a.Id, Assert.IsType<DrawNode>(Single(scene, 24)).Item);
        var first = Assert.IsType<TransitionNode>(Single(scene, 25));
        Assert.Equal(0, first.Progress);
        var middle = Assert.IsType<TransitionNode>(Single(scene, 30));
        Assert.Equal(0.5, middle.Progress);
        Assert.Equal(a.Id, Assert.IsType<DrawNode>(middle.From).Item);
        Assert.Equal(b.Id, Assert.IsType<DrawNode>(middle.To).Item);
        Assert.Equal("nmm.crossfade", middle.Type);
        Assert.Equal(0.9, Assert.IsType<TransitionNode>(Single(scene, 34)).Progress, 9);
        Assert.Equal(b.Id, Assert.IsType<DrawNode>(Single(scene, 35)).Item);
    }

    [Fact]
    public void StartsAtTheItemWhenNothingComesBefore()
    {
        // 前に隣り合うアイテムがない（すき間がある）ときは、アイテムの始まりから何もないところから切り替える
        var a = Image(0, 20);
        var b = Image(30, 30, Crossfade(10));
        var scene = With(a, b);

        Assert.Empty(TimelineEvaluator.Evaluate(scene, 25).Nodes);
        var start = Assert.IsType<TransitionNode>(Single(scene, 30));
        Assert.Equal(0, start.Progress);
        Assert.Null(start.From);
        Assert.Equal(0.5, Assert.IsType<TransitionNode>(Single(scene, 35)).Progress);
        Assert.IsType<DrawNode>(Single(scene, 40));
    }

    [Fact]
    public void ShortensTheTransitionToFitShortNeighbors()
    {
        // 前のアイテムが 6 フレームなので、20 フレームのトランジションは 6 フレーム（[27, 33)）にする
        var a = Image(24, 6);
        var b = Image(30, 30, Crossfade(20));
        var scene = With(a, b);

        Assert.Equal((27L, 6L), TimelineEvaluator.TransitionWindow(scene, b) is { } window ? (window.Start, window.Duration) : default);
        Assert.IsType<DrawNode>(Single(scene, 26));
        Assert.Equal(0.5, Assert.IsType<TransitionNode>(Single(scene, 30)).Progress);
    }

    [Fact]
    public void ShowsTheSourceBeyondTheCutDuringTheTransition()
    {
        // 前の動画は、切れ目より後ろも素材の続きを表示する
        var video = new VideoItem(ItemId.New(), Empty.Layers[0].Id, 0, 30, AssetId.New());
        var b = Image(30, 30, Crossfade(10));
        var scene = With(video, b);

        var node = Assert.IsType<TransitionNode>(Single(scene, 32));
        var content = Assert.IsType<VideoFrameContent>(Assert.IsType<DrawNode>(node.From).Content);
        Assert.Equal(new Rational(32, 30), content.SourceTime);
    }

    [Fact]
    public void UnknownTransitionsCutDirectly()
    {
        var a = Image(0, 30);
        var b = Image(30, 30, new TransitionInstance("plugin.unknown", 10));
        var scene = With(a, b);

        Assert.Equal(a.Id, Assert.IsType<DrawNode>(Single(scene, 29)).Item);
        Assert.Equal(b.Id, Assert.IsType<DrawNode>(Single(scene, 30)).Item);
    }

    [Fact]
    public void KeepsTheLayerOrder()
    {
        var below = Image(0, 60, layer: 0);
        var a = Image(0, 30, layer: 1);
        var b = Image(30, 30, Crossfade(10), layer: 1);
        var nodes = TimelineEvaluator.Evaluate(With(below, a, b), 30).Nodes;

        Assert.Equal(2, nodes.Length);
        Assert.Equal(below.Id, Assert.IsType<DrawNode>(nodes[0]).Item);
        Assert.IsType<TransitionNode>(nodes[1]);
    }

    [Fact]
    public void EvaluatesParametersWithDefaults()
    {
        var wipe = new TransitionInstance("nmm.wipe", 10, ImmutableDictionary<string, ParameterValue>.Empty.Add("direction", new TextParameter("fromTop")));
        var scene = With(Image(0, 30), Image(30, 30, wipe));

        var parameters = Assert.IsType<TransitionNode>(Single(scene, 30)).Parameters;

        Assert.Equal("fromTop", parameters.GetText("direction"));
        Assert.Equal(10, parameters.GetNumber("softness"));
    }
}
