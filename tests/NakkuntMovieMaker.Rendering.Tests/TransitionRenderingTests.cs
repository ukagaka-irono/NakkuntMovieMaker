using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Rendering.Transitions;

namespace NakkuntMovieMaker.Rendering.Tests;

public class TransitionRenderingTests
{
    // 160×90・30fps。前のアイテムは [0, 30)、後のアイテムは [30, 60)。
    // 20 フレームのトランジションは [20, 40) で、25 フレーム目が 1/4、30 フレーム目が半分
    private static readonly SceneFormat Small = new(160, 90, 30, 48000);
    private static readonly Scene Empty = Scene.CreateNew("テスト", Small, 1);
    private static readonly RgbaColor Red = new(255, 0, 0);
    private static readonly RgbaColor Blue = new(0, 0, 255);

    private readonly SkiaCpuBackend _backend = new();
    private readonly TestMedia _media = new();

    private static TransitionInstance Transition(TransitionDefinition definition, params (string Name, ParameterValue Value)[] values) =>
        new(definition.Type, 20, values.ToImmutableDictionary(value => value.Name, value => value.Value));

    // 画面全体を塗る背景
    private static ShapeItem Fill(RgbaColor color, long start, TransitionInstance? transition = null) =>
        new(ItemId.New(), Empty.Layers[0].Id, start, 30, new ShapeStyle(ShapeKind.Background, 0, 0, new SolidPaint(color))) { Transition = transition };

    // 中心に置いた 20×20 の正方形
    private static ShapeItem Square(RgbaColor color, long start, TransitionInstance? transition = null) =>
        new(ItemId.New(), Empty.Layers[0].Id, start, 30, new ShapeStyle(ShapeKind.Rectangle, 20, 20, new SolidPaint(color))) { Transition = transition };

    private BgraImage Render(long frame, params Item[] items)
    {
        var scene = Empty with { Items = Empty.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };
        return _backend.Render(TimelineEvaluator.Evaluate(scene, frame), _media, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void CrossfadeMixesBothItems()
    {
        var image = Render(30, Fill(Red, 0), Fill(Blue, 30, Transition(BuiltInTransitions.Crossfade)));

        Pixels.AssertColor(image, 80, 45, (128, 0, 128, 255), tolerance: 1);
    }

    [Fact]
    public void CrossfadeFromNothingFadesIn()
    {
        // 前に何もないときは、アイテムの始まりから 20 フレームで現れる：10 フレーム目で半分
        var image = Render(40, Fill(Blue, 30, Transition(BuiltInTransitions.Crossfade)));

        Pixels.AssertColor(image, 80, 45, (0, 0, 128, 255), tolerance: 1);
    }

    [Theory]
    [InlineData(25, 128, 0, 0)] // 前半：赤が黒に近づく
    [InlineData(30, 0, 0, 0)] // 半分で黒だけ
    [InlineData(35, 0, 0, 128)] // 後半：黒から青へ
    public void DipGoesThroughTheColor(long frame, int r, int g, int b)
    {
        var image = Render(frame, Fill(Red, 0), Fill(Blue, 30, Transition(BuiltInTransitions.Dip)));

        Pixels.AssertColor(image, 80, 45, (r, g, b, 255), tolerance: 1);
    }

    [Fact]
    public void WipeRevealsFromTheChosenSide()
    {
        var wipe = Transition(BuiltInTransitions.Wipe, ("softness", new NumberParameter(0.0)));

        var image = Render(30, Fill(Red, 0), Fill(Blue, 30, wipe));

        Pixels.AssertColor(image, 40, 45, (0, 0, 255, 255), tolerance: 1); // 左（入ってくる側）は後のアイテム
        Pixels.AssertColor(image, 120, 45, (255, 0, 0, 255), tolerance: 1);

        var fromBottom = Transition(BuiltInTransitions.Wipe, ("softness", new NumberParameter(0.0)), ("direction", new TextParameter("fromBottom")));
        var vertical = Render(30, Fill(Red, 0), Fill(Blue, 30, fromBottom));
        Pixels.AssertColor(vertical, 80, 70, (0, 0, 255, 255), tolerance: 1);
        Pixels.AssertColor(vertical, 80, 20, (255, 0, 0, 255), tolerance: 1);
    }

    [Fact]
    public void SlideMovesOnlyTheNextItem()
    {
        // 右から入る：後の正方形は中心から右へ半分（80 ピクセル）ずれた所、前の正方形は動かない
        var image = Render(30, Square(RgbaColor.White, 0), Square(Red, 30, Transition(BuiltInTransitions.Slide)));

        Pixels.AssertColor(image, 80, 45, (255, 255, 255, 255), tolerance: 1);
        Pixels.AssertColor(image, 155, 45, (255, 0, 0, 255), tolerance: 1);
    }

    [Fact]
    public void PushMovesBothItems()
    {
        // 前の正方形は左へ 80 ピクセル押し出され、後の正方形は右から 80 ピクセルの所まで来ている
        var image = Render(30, Square(RgbaColor.White, 0), Square(Red, 30, Transition(BuiltInTransitions.Push)));

        Pixels.AssertColor(image, 4, 45, (255, 255, 255, 255), tolerance: 1);
        Pixels.AssertColor(image, 80, 45, (0, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 155, 45, (255, 0, 0, 255), tolerance: 1);
    }

    [Fact]
    public void SpinShrinksTheFirstItemInTheFirstHalf()
    {
        // 1/4 の時点で、前の正方形は半分の大きさ（10×10）で 45 度回っている
        var image = Render(25, Square(RgbaColor.White, 0), Square(Red, 30, Transition(BuiltInTransitions.Spin)));

        Pixels.AssertColor(image, 80, 45, (255, 255, 255, 255), tolerance: 1);
        Pixels.AssertColor(image, 80, 40, (255, 255, 255, 255), tolerance: 1); // 45 度回した正方形は、上の角が中心から約 7 ピクセル上にある
        Pixels.AssertColor(image, 74, 39, (0, 0, 0, 255), tolerance: 1); // 回す前の角の位置は空く
    }

    // 各トランジションのゴールデン画像（半分の時点。回転と色を挟んだフェードは、半分では何も見えないので 1/4 の時点）
    public static TheoryData<string> GoldenTransitions => new(BuiltInTransitions.All.Select(transition => transition.Type));

    [Theory]
    [MemberData(nameof(GoldenTransitions))]
    public void MatchesTheGoldenImage(string type)
    {
        var definition = BuiltInTransitions.All.Single(transition => transition.Type == type);
        var from = new ImageItem(ItemId.New(), Empty.Layers[0].Id, 0, 30, _media.AddImage(TestImages.Subject(120, 72)));
        var to = new ImageItem(ItemId.New(), Empty.Layers[0].Id, 30, 30, _media.AddImage(TestImages.Quadrants(72, 72)))
        {
            Transition = Transition(definition),
        };

        var image = Render(type == BuiltInTransitions.Spin.Type || type == BuiltInTransitions.Dip.Type ? 25 : 30, from, to);

        Golden.AssertMatches(image, "transition-" + type["nmm.".Length..]);
    }
}
