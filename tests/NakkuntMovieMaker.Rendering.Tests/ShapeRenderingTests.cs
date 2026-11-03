using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Tests;

public class ShapeRenderingTests
{
    private static readonly SceneFormat Small = new(320, 180, 30, 48000);
    private static readonly Scene Empty = Scene.CreateNew("テスト", Small, 10);
    private static readonly RgbaColor Red = new(255, 0, 0);

    private readonly SkiaCpuBackend _backend = new();
    private readonly TestMedia _media = new();

    private static ShapeItem Shape(ShapeStyle shape, int layer = 0, double x = 0, double y = 0) =>
        new(ItemId.New(), Empty.Layers[layer].Id, 0, 30, shape) { Transform = Transform2D.Identity with { X = x, Y = y } };

    private BgraImage Render(RgbaColor background, params Item[] items)
    {
        var scene = Empty with { Items = Empty.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };
        return _backend.Render(TimelineEvaluator.Evaluate(scene, 0, background), _media, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void FillsARectangleAtTheCenter()
    {
        // 100×50 は x 110〜209・y 65〜114
        var image = Render(RgbaColor.Black, Shape(new ShapeStyle(ShapeKind.Rectangle, 100, 50, new SolidPaint(Red))));

        Pixels.AssertColor(image, 110, 65, (255, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 209, 114, (255, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 109, 90, (0, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 210, 90, (0, 0, 0, 255), tolerance: 0);
    }

    [Fact]
    public void BackgroundCoversTheWholeScene()
    {
        var image = Render(RgbaColor.Black, Shape(new ShapeStyle(ShapeKind.Background, 10, 10, new SolidPaint(Red))));

        Pixels.AssertColor(image, 0, 0, (255, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 319, 179, (255, 0, 0, 255), tolerance: 0);
    }

    [Fact]
    public void CropsShapesAroundTheVisibleCenter()
    {
        var item = Shape(new ShapeStyle(ShapeKind.Rectangle, 100, 50, new SolidPaint(Red))) with { Crop = new Crop(0, 0, 50, 0) };

        var image = Render(RgbaColor.Black, item);

        // 残った 50×50 が中心に来る：x 135〜184
        Pixels.AssertColor(image, 136, 90, (255, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 183, 90, (255, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 133, 90, (0, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, 186, 90, (0, 0, 0, 255), tolerance: 0);
    }

    [Fact]
    public void PaintsGradientsAlongTheAngle()
    {
        var gradient = new LinearGradientPaint(RgbaColor.Black, RgbaColor.White);

        var horizontal = Render(Red, Shape(new ShapeStyle(ShapeKind.Rectangle, 200, 100, gradient)));
        Pixels.AssertColor(horizontal, 61, 90, (1, 1, 1, 255), tolerance: 3);
        Pixels.AssertColor(horizontal, 160, 90, (128, 128, 128, 255), tolerance: 3);
        Pixels.AssertColor(horizontal, 258, 90, (254, 254, 254, 255), tolerance: 3);

        // 90 度で上から下
        var vertical = Render(Red, Shape(new ShapeStyle(ShapeKind.Rectangle, 200, 100, gradient with { Angle = 90 })));
        Pixels.AssertColor(vertical, 160, 40, (1, 1, 1, 255), tolerance: 3);
        Pixels.AssertColor(vertical, 160, 139, (254, 254, 254, 255), tolerance: 3);
    }

    [Fact]
    public void AppliesOpacityToTheShapeAsAWhole()
    {
        // 白の塗りと黒の線を重ねた図形を半透明にする。塗りと線を別々に半透明にすると、
        // 線の内側の半分に塗りの白が透けるが、まとめて半透明にすれば黒と赤の中間になる
        var shape = new ShapeStyle(ShapeKind.Rectangle, 100, 50, new SolidPaint(RgbaColor.White), RgbaColor.Black, StrokeWidth: 20);
        var item = Shape(shape) with { Opacity = 0.5 };

        var image = Render(Red, item);

        Pixels.AssertColor(image, 115, 90, (128, 0, 0, 255), tolerance: 2); // 線の内側の半分
        Pixels.AssertColor(image, 160, 90, (255, 128, 128, 255), tolerance: 1); // 塗りだけの部分
    }

    [Fact]
    public void MatchesGoldenShapes()
    {
        var white = new SolidPaint(RgbaColor.White);
        var orange = new RgbaColor(255, 140, 0);
        var image = Render(
            new RgbaColor(32, 36, 48),
            Shape(new ShapeStyle(ShapeKind.Rectangle, 70, 50, new SolidPaint(new RgbaColor(80, 160, 255)), RgbaColor.White, 3, CornerRadius: 12), 0, -110, -45),
            Shape(new ShapeStyle(ShapeKind.Ellipse, 70, 50, new LinearGradientPaint(orange, new RgbaColor(200, 0, 120), 45)), 1, -30, -45),
            Shape(new ShapeStyle(ShapeKind.Triangle, 60, 55, white, orange, 4), 2, 45, -45),
            Shape(new ShapeStyle(ShapeKind.Polygon, 60, 60, new SolidPaint(new RgbaColor(120, 220, 120)), Sides: 6), 3, 115, -45),
            Shape(new ShapeStyle(ShapeKind.Star, 64, 64, new SolidPaint(new RgbaColor(255, 220, 0)), new RgbaColor(160, 80, 0), 2, Sides: 5, InnerRadiusRatio: 0.45), 4, -110, 40),
            Shape(new ShapeStyle(ShapeKind.Line, 80, 6, white), 5, -30, 40),
            Shape(new ShapeStyle(ShapeKind.Arrow, 80, 40, new SolidPaint(new RgbaColor(255, 80, 80))), 6, 55, 40),
            Shape(new ShapeStyle(ShapeKind.Star, 50, 50, new SolidPaint(new RgbaColor(200, 120, 255)), Sides: 8, InnerRadiusRatio: 0.7), 7, 125, 40));

        Golden.AssertMatches(image, "shapes");
    }
}
