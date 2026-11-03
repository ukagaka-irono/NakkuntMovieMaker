using System.Numerics;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Tests;

public class TimelineEvaluatorTests
{
    // 1920×1080・30fps、レイヤー 3 つ
    private static readonly Scene Empty = Scene.CreateNew("テスト", SceneFormat.Landscape1080p30, 3);

    private static LayerId LayerAt(int index) => Empty.Layers[index].Id;

    private static Scene With(params Item[] items) => With(Empty, items);

    private static Scene With(Scene scene, params Item[] items) =>
        scene with { Items = scene.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };

    private static ImageItem Image(int layer = 0, long start = 0, long length = 30) =>
        new(ItemId.New(), LayerAt(layer), start, length, AssetId.New());

    private static VideoItem Video(MediaPlayback playback, long start = 0, long length = 30) =>
        new(ItemId.New(), LayerAt(0), start, length, AssetId.New()) { Playback = playback };

    private static DrawNode Single(FramePlan plan) => Assert.IsType<DrawNode>(Assert.Single(plan.Nodes));

    private static Vector2 Map(DrawNode node, float x, float y) => Vector2.Transform(new Vector2(x, y), node.Transform);

    private static void AssertNear(Vector2 expected, Vector2 actual) =>
        Assert.True(Vector2.Distance(expected, actual) < 1e-3f, $"期待 {expected}、実際 {actual}");

    [Fact]
    public void EmptySceneHasOnlyTheBackground()
    {
        var plan = TimelineEvaluator.Evaluate(Empty, 0);

        Assert.Equal((1920, 1080, RgbaColor.Black), (plan.Width, plan.Height, plan.Background));
        Assert.Empty(plan.Nodes);
        Assert.Equal(RgbaColor.Transparent, TimelineEvaluator.Evaluate(Empty, 0, RgbaColor.Transparent).Background);
    }

    [Fact]
    public void DrawsItemsFromTheBottomLayerUp()
    {
        var top = Image(layer: 2);
        var bottom = Image(layer: 0);
        var middle = Image(layer: 1, start: 10);
        var scene = With(top, bottom, middle);

        Assert.Equal([bottom.Id, top.Id], TimelineEvaluator.Evaluate(scene, 0).Nodes.Cast<DrawNode>().Select(node => node.Item));
        Assert.Equal([bottom.Id, middle.Id, top.Id], TimelineEvaluator.Evaluate(scene, 10).Nodes.Cast<DrawNode>().Select(node => node.Item));
        // アイテムの終わりのフレームは含まない
        Assert.Equal([middle.Id], TimelineEvaluator.Evaluate(scene, 30).Nodes.Cast<DrawNode>().Select(node => node.Item));
    }

    [Fact]
    public void SkipsHiddenLayersAndShowsOnlySoloLayers()
    {
        var first = Image(layer: 0);
        var second = Image(layer: 1);
        var third = Image(layer: 2);
        var scene = With(first, second, third);

        var hidden = scene with { Layers = scene.Layers.SetItem(1, scene.Layers[1] with { Visible = false }) };
        Assert.Equal([first.Id, third.Id], TimelineEvaluator.Evaluate(hidden, 0).Nodes.Cast<DrawNode>().Select(node => node.Item));

        var solo = scene with { Layers = scene.Layers.SetItem(2, scene.Layers[2] with { Solo = true }) };
        Assert.Equal([third.Id], TimelineEvaluator.Evaluate(solo, 0).Nodes.Cast<DrawNode>().Select(node => node.Item));
    }

    [Fact]
    public void IdentityTransformPutsTheItemAtTheCenter()
    {
        var node = Single(TimelineEvaluator.Evaluate(With(Image()), 0));

        AssertNear(new Vector2(960, 540), Map(node, 0, 0));
        AssertNear(new Vector2(970, 545), Map(node, 10, 5));
        Assert.Equal(1, node.Opacity);
        Assert.Equal(BlendMode.Normal, node.Blend);
    }

    [Fact]
    public void ScalesAndRotatesClockwiseAroundTheAnchor()
    {
        var item = Image() with
        {
            Transform = Transform2D.Identity with { X = 100.0, Y = -50.0, ScaleX = 2.0, ScaleY = 2.0, Rotation = 90.0 },
        };

        var node = Single(TimelineEvaluator.Evaluate(With(item), 0));

        AssertNear(new Vector2(1060, 490), Map(node, 0, 0));
        // 右へ 10 → 2 倍で 20 → 時計回りに 90 度で下へ 20
        AssertNear(new Vector2(1060, 510), Map(node, 10, 0));

        // 中心点を右へ 50 ずらすと、その点が位置に来て、そのまわりに回る
        var anchored = item with { Transform = item.Transform with { AnchorX = 50.0 } };
        var anchoredNode = Single(TimelineEvaluator.Evaluate(With(anchored), 0));
        AssertNear(new Vector2(1060, 490), Map(anchoredNode, 50, 0));
        AssertNear(new Vector2(1060, 390), Map(anchoredNode, 0, 0));
    }

    [Fact]
    public void FlipsAroundTheCenterOfTheItem()
    {
        var item = Image() with { Transform = Transform2D.Identity with { FlipHorizontal = true, X = 100.0 } };

        var node = Single(TimelineEvaluator.Evaluate(With(item), 0));

        AssertNear(new Vector2(1050, 545), Map(node, 10, 5));

        var both = item with { Transform = item.Transform with { FlipVertical = true } };
        AssertNear(new Vector2(1050, 535), Map(Single(TimelineEvaluator.Evaluate(With(both), 0)), 10, 5));
    }

    [Fact]
    public void EvaluatesKeyframesRelativeToTheItemStart()
    {
        var item = Image(start: 100, length: 30) with
        {
            Opacity = Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(10, 1)),
            Transform = Transform2D.Identity with
            {
                X = Animatable.FromKeys(new Keyframe<double>(0, 0, Easing.Linear), new Keyframe<double>(20, 200)),
            },
        };
        var scene = With(item);

        // 先頭のフレームは不透明度 0 なので描かない
        Assert.Empty(TimelineEvaluator.Evaluate(scene, 100).Nodes);

        var node = Single(TimelineEvaluator.Evaluate(scene, 105));
        Assert.Equal(0.5, node.Opacity, 9);
        AssertNear(new Vector2(1010, 540), Map(node, 0, 0));

        var later = Single(TimelineEvaluator.Evaluate(scene, 125));
        Assert.Equal(1, later.Opacity);
        AssertNear(new Vector2(1160, 540), Map(later, 0, 0));
    }

    [Fact]
    public void ClampsOpacityAndSkipsInvisibleItems()
    {
        var overshoot = Image() with { Opacity = 1.5 };
        Assert.Equal(1, Single(TimelineEvaluator.Evaluate(With(overshoot), 0)).Opacity);

        var flat = Image() with { Transform = Transform2D.Identity with { ScaleY = 0.0 } };
        Assert.Empty(TimelineEvaluator.Evaluate(With(flat), 0).Nodes);
    }

    [Fact]
    public void PassesCropAndBlendModeThrough()
    {
        var item = Image() with { Crop = new Crop(10, 20, 30, 40), Blend = BlendMode.Screen };

        var node = Single(TimelineEvaluator.Evaluate(With(item), 0));

        Assert.Equal(new Crop(10, 20, 30, 40), node.Crop);
        Assert.Equal(BlendMode.Screen, node.Blend);
        Assert.Equal(new ImageContent(item.Asset), node.Content);
    }

    [Theory]
    [InlineData(0, 2, 1, false, 0, "2")] // 素材の 2 秒から等速
    [InlineData(0, 2, 1, false, 15, "5/2")]
    [InlineData(0, 2, 2, false, 15, "3")] // 2 倍速：15 フレーム（0.5 秒）で素材は 1 秒進む
    [InlineData(0, 2, "1/2", false, 15, "9/4")] // スロー
    [InlineData(10, 2, 1, false, 25, "5/2")] // アイテムが 10 フレーム目から始まる
    [InlineData(0, 2, 1, true, 0, "89/30")] // 逆再生：最初は範囲 [2, 3) の最後のフレーム
    [InlineData(0, 2, 1, true, 29, "2")] // 逆再生：最後は範囲の最初のフレーム
    public void MapsTimelineFramesToSourceTime(long start, long offset, object speed, bool reverse, long frame, string expected)
    {
        var item = Video(new MediaPlayback(offset, Rational.Parse(speed.ToString()!), reverse), start: start, length: 30);

        var content = Assert.IsType<VideoFrameContent>(Single(TimelineEvaluator.Evaluate(With(item), frame)).Content);

        Assert.Equal(Rational.Parse(expected), content.SourceTime);
        Assert.Equal(item.Asset, content.Asset);
    }

    [Fact]
    public void RevealsTextOneCharacterAtATime()
    {
        var style = TextStyle.Default with { RevealCharsPerSecond = 10 };
        var item = new TextItem(ItemId.New(), LayerAt(0), 0, 90, "こんにちは👋世界") { Style = style };
        var scene = With(item);

        int? Visible(long frame) => Assert.IsType<TextContent>(Single(TimelineEvaluator.Evaluate(scene, frame)).Content).VisibleCharacters;

        Assert.Equal(0, Visible(0));
        Assert.Equal(5, Visible(15)); // 0.5 秒で 5 文字
        Assert.Equal(6, Visible(18)); // 絵文字（サロゲートペア）も 1 文字
        Assert.Equal(8, Visible(60)); // 全部で 8 文字

        var instant = item with { Style = TextStyle.Default };
        Assert.Null(Assert.IsType<TextContent>(Single(TimelineEvaluator.Evaluate(With(instant), 0)).Content).VisibleCharacters);
    }

    [Fact]
    public void DrawsShapesAndSkipsItemsWithoutPictures()
    {
        var shape = new ShapeItem(ItemId.New(), LayerAt(0), 0, 30, new ShapeStyle(ShapeKind.Ellipse, 100, 50, new SolidPaint(RgbaColor.White)));
        var audio = new AudioItem(ItemId.New(), LayerAt(1), 0, 30, AssetId.New());

        var node = Single(TimelineEvaluator.Evaluate(With(shape, audio), 0));

        Assert.Equal(new ShapeContent(shape.Shape), node.Content);
    }

    [Fact]
    public void UnchangingFramesHaveEqualPlans()
    {
        var still = Image(length: 60);
        var scene = With(still);

        // 止まっている画像だけの区間は、同じ FramePlan になる（描画した結果を使い回せる）
        Assert.Equal(TimelineEvaluator.Evaluate(scene, 3), TimelineEvaluator.Evaluate(scene, 40));

        var moving = With(Video(MediaPlayback.Normal, length: 60));
        Assert.NotEqual(TimelineEvaluator.Evaluate(moving, 3), TimelineEvaluator.Evaluate(moving, 40));
    }
}
