using System.Collections.Immutable;
using System.Globalization;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Tests;

public class EffectRenderingTests
{
    // 160×90・30fps。中心は (80, 45)
    private static readonly SceneFormat Small = new(160, 90, 30, 48000);
    private static readonly Scene Empty = Scene.CreateNew("テスト", Small, 1);
    private static readonly RgbaColor Backdrop = new(40, 40, 48);

    private readonly SkiaCpuBackend _backend = new();
    private readonly TestMedia _media = new();

    private static EffectInstance Effect(EffectDefinition definition, params (string Name, object Value)[] values) =>
        new(EffectId.New(), definition.Type, values.ToImmutableDictionary(
            value => value.Name,
            value => value.Value switch
            {
                double number => (ParameterValue)new NumberParameter(number),
                int number => new NumberParameter((double)number),
                RgbaColor color => new ColorParameter(color),
                bool flag => new BoolParameter(flag),
                string text => new TextParameter(text),
                _ => throw new ArgumentException("扱えない値です", nameof(values)),
            }));

    private ImageItem Image(BgraImage image, params EffectInstance[] effects) =>
        new(ItemId.New(), Empty.Layers[0].Id, 0, 90, _media.AddImage(image)) { Effects = [.. effects] };

    // 中心に置いた正方形（20×20 なら x 70〜89・y 35〜54）
    private static ShapeItem Square(int size, RgbaColor color, params EffectInstance[] effects) =>
        new(ItemId.New(), Empty.Layers[0].Id, 0, 90, new ShapeStyle(ShapeKind.Rectangle, size, size, new SolidPaint(color))) { Effects = [.. effects] };

    private static ShapeItem Star(params EffectInstance[] effects) =>
        new(ItemId.New(), Empty.Layers[0].Id, 0, 90, new ShapeStyle(ShapeKind.Star, 50, 50, new SolidPaint(new RgbaColor(255, 210, 40)), InnerRadiusRatio: 0.45))
        {
            Effects = [.. effects],
        };

    private BgraImage Render(Item item, RgbaColor? background = null, long frame = 0)
    {
        var scene = Empty with { Items = Empty.Items.Add(item.Id, item) };
        return _backend.Render(TimelineEvaluator.Evaluate(scene, frame, background ?? RgbaColor.Black), _media, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static int Brightness(BgraImage image, int x, int y) => image.GetPixel(x, y).R;

    [Fact]
    public void ColorAdjustBrightensAndKeepsAlpha()
    {
        var gray = TestImages.Solid(32, 32, (100, 100, 100, 255));
        var adjusted = Render(Image(gray, Effect(BuiltInEffects.ColorAdjust, ("brightness", 50))));
        Pixels.AssertColor(adjusted, 80, 45, (227, 227, 227, 255), tolerance: 2);

        // 半透明の画素は、不透明度はそのままで色だけ変わる
        var translucent = TestImages.Solid(32, 32, (100, 100, 100, 128));
        var onTransparent = Render(Image(translucent, Effect(BuiltInEffects.ColorAdjust, ("brightness", 50))), RgbaColor.Transparent);
        Pixels.AssertColor(onTransparent, 80, 45, (114, 114, 114, 128), tolerance: 2);
    }

    [Fact]
    public void MonochromeUsesTheLuminance()
    {
        var red = TestImages.Solid(32, 32, (255, 0, 0, 255));

        Pixels.AssertColor(Render(Image(red, Effect(BuiltInEffects.Monochrome))), 80, 45, (54, 54, 54, 255), tolerance: 1);
        Pixels.AssertColor(Render(Image(red, Effect(BuiltInEffects.Monochrome, ("strength", 50)))), 80, 45, (155, 27, 27, 255), tolerance: 1);
    }

    [Fact]
    public void ChromaKeyRemovesTheKeyColor()
    {
        var image = TestImages.Halves(32, 32, (0, 255, 0, 255), (255, 0, 0, 255));

        var keyed = Render(Image(image, Effect(BuiltInEffects.ChromaKey)), RgbaColor.White);

        Pixels.AssertColor(keyed, 68, 45, (255, 255, 255, 255), tolerance: 1);
        Pixels.AssertColor(keyed, 90, 45, (255, 0, 0, 255), tolerance: 1);
    }

    [Fact]
    public void ColorKeyRemovesColorsCloseToTheKey()
    {
        var image = TestImages.Halves(32, 32, (250, 250, 250, 255), (0, 0, 255, 255));

        var keyed = Render(Image(image, Effect(BuiltInEffects.ColorKey)));

        Pixels.AssertColor(keyed, 68, 45, (0, 0, 0, 255), tolerance: 1);
        Pixels.AssertColor(keyed, 90, 45, (0, 0, 255, 255), tolerance: 1);
    }

    [Fact]
    public void BlurSpreadsOutsideTheShape()
    {
        var blurred = Render(Square(20, RgbaColor.White, Effect(BuiltInEffects.Blur, ("radius", 10))));

        Assert.InRange(Brightness(blurred, 65, 45), 10, 200); // 端から 5 ピクセル外
        Assert.Equal(0, Brightness(blurred, 50, 45)); // 半径より外
        Assert.InRange(Brightness(blurred, 80, 45), 180, 255);
    }

    [Fact]
    public void DropShadowIsOffsetBelowTheShape()
    {
        var shadow = Effect(BuiltInEffects.DropShadow, ("offsetX", 10), ("offsetY", 10), ("blur", 0), ("opacity", 100));

        var image = Render(Square(20, RgbaColor.White, shadow), RgbaColor.White);

        Pixels.AssertColor(image, 95, 60, (0, 0, 0, 255), tolerance: 1);
        Pixels.AssertColor(image, 80, 45, (255, 255, 255, 255), tolerance: 0);
        Pixels.AssertColor(image, 75, 60, (255, 255, 255, 255), tolerance: 0); // 影の外
    }

    [Fact]
    public void BorderSurroundsTheShapeWithTheWidth()
    {
        var border = Effect(BuiltInEffects.Border, ("color", new RgbaColor(255, 0, 0)), ("width", 6));

        var image = Render(Square(20, RgbaColor.White, border));

        Pixels.AssertColor(image, 66, 45, (255, 0, 0, 255), tolerance: 1); // 4 ピクセル外
        Pixels.AssertColor(image, 60, 45, (0, 0, 0, 255), tolerance: 0); // 10 ピクセル外
        Pixels.AssertColor(image, 80, 45, (255, 255, 255, 255), tolerance: 0);
        // 角は丸く縁取られる：角から斜めに 6 ピクセル（各方向 4.2）は縁の外
        Pixels.AssertColor(image, 64, 29, (0, 0, 0, 255), tolerance: 0);
    }

    [Fact]
    public void GlowLightsUpAroundTheShape()
    {
        var image = Render(Square(20, RgbaColor.White, Effect(BuiltInEffects.Glow, ("radius", 12))));

        Assert.InRange(Brightness(image, 66, 45), 30, 254);
        Pixels.AssertColor(image, 80, 45, (255, 255, 255, 255), tolerance: 0);
    }

    [Fact]
    public void MaskKeepsOnlyTheInsideOrOutside()
    {
        var mask = Effect(BuiltInEffects.Mask, ("width", 20), ("height", 20));
        var inside = Render(Square(40, RgbaColor.White, mask));
        Pixels.AssertColor(inside, 80, 45, (255, 255, 255, 255), tolerance: 0);
        Pixels.AssertColor(inside, 65, 45, (0, 0, 0, 255), tolerance: 0);

        var inverted = Effect(BuiltInEffects.Mask, ("width", 20), ("height", 20), ("invert", true));
        var outside = Render(Square(40, RgbaColor.White, inverted));
        Pixels.AssertColor(outside, 80, 45, (0, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(outside, 65, 45, (255, 255, 255, 255), tolerance: 0);
    }

    [Fact]
    public void FadeChangesTheOpacityOverTime()
    {
        var item = Square(20, RgbaColor.White, Effect(BuiltInEffects.Fade, ("fadeIn", 1.0), ("fadeOut", 0.0)));

        Assert.Equal(0, Brightness(Render(item, frame: 0), 80, 45));
        Assert.InRange(Brightness(Render(item, frame: 15), 80, 45), 126, 130);
        Assert.Equal(255, Brightness(Render(item, frame: 60), 80, 45));
    }

    [Fact]
    public void NoiseIsDeterministicPerFrame()
    {
        var subject = TestImages.Solid(32, 32, (128, 128, 128, 255));
        var noisy = Image(subject, Effect(BuiltInEffects.Noise, ("amount", 30)));

        Assert.Equal(Render(noisy, frame: 1).Pixels, Render(noisy, frame: 1).Pixels);
        Assert.NotEqual(Render(noisy, frame: 1).Pixels, Render(noisy, frame: 2).Pixels);

        var silent = Image(subject, Effect(BuiltInEffects.Noise, ("amount", 0)));
        Assert.Equal(Render(Image(subject)).Pixels, Render(silent).Pixels);
    }

    [Fact]
    public void ShakeMovesTheItemByTheEvaluatedOffset()
    {
        var item = Square(20, RgbaColor.White, Effect(BuiltInEffects.Shake, ("amplitudeX", 20), ("amplitudeY", 0), ("frequency", 30)));
        var scene = Empty with { Items = Empty.Items.Add(item.Id, item) };

        // ずれが 3 ピクセル以上のフレームで確かめる
        var (frame, offset) = Enumerable.Range(0, 30)
            .Select(f => (Frame: f, Offset: ((DrawNode)TimelineEvaluator.Evaluate(scene, f).Nodes[0]).Effects[0].Parameters.GetNumber("offsetX")))
            .First(entry => Math.Abs(entry.Offset) >= 3);
        var image = Render(item, frame: frame);

        var left = 70 + (int)Math.Round(offset);
        Assert.Equal(255, Brightness(image, left + 2, 45));
        Assert.Equal(0, Brightness(image, left - 2, 45));
    }

    [Fact]
    public void MosaicAveragesEachBlock()
    {
        // 4 ピクセルごとの赤と青のしま模様は、16 ピクセルのブロックで紫になる
        var stripes = TestImages.Stripes(32, 32, 4, (255, 0, 0, 255), (0, 0, 255, 255));

        var image = Render(Image(stripes, Effect(BuiltInEffects.Mosaic, ("size", 16))));

        Pixels.AssertColor(image, 72, 40, (128, 0, 128, 255), tolerance: 2);
        Pixels.AssertColor(image, 88, 50, (128, 0, 128, 255), tolerance: 2);
    }

    [Fact]
    public void SharpenIncreasesContrastAtEdges()
    {
        var halves = TestImages.Halves(32, 32, (100, 100, 100, 255), (200, 200, 200, 255));

        var image = Render(Image(halves, Effect(BuiltInEffects.Sharpen, ("amount", 200), ("radius", 3))));

        Assert.InRange(Brightness(image, 79, 45), 0, 94); // 境界のすぐ左は暗く
        Assert.InRange(Brightness(image, 80, 45), 206, 255); // すぐ右は明るく
        Assert.InRange(Brightness(image, 66, 45), 98, 102); // 境界から離れたところは変わらない
    }

    [Fact]
    public void LutTransformsColors()
    {
        using var folder = new TempFiles();
        var identity = folder.Write("identity.cube", TestLuts.Cube(2, rgb => rgb));
        var invert = folder.Write("invert.cube", TestLuts.Cube(2, rgb => (1 - rgb.R, 1 - rgb.G, 1 - rgb.B)));
        var subject = TestImages.Subject(96, 60);

        var original = Render(Image(subject));
        var same = Render(Image(subject, Effect(BuiltInEffects.Lut, ("file", identity))));
        var inverted = Render(Image(subject, Effect(BuiltInEffects.Lut, ("file", invert))));
        var missing = Render(Image(subject, Effect(BuiltInEffects.Lut, ("file", folder.Combine("missing.cube")))));

        foreach (var (x, y) in new[] { (40, 25), (80, 25), (110, 25), (50, 60), (115, 60) })
        {
            var (b, g, r, _) = original.GetPixel(x, y);
            Pixels.AssertColor(same, x, y, (r, g, b, 255), tolerance: 1);
            Pixels.AssertColor(inverted, x, y, (255 - r, 255 - g, 255 - b, 255), tolerance: 2);
        }

        Assert.Equal(original.Pixels, missing.Pixels);
    }
}
