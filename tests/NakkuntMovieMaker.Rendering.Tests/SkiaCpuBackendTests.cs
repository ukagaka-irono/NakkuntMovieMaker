using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Tests;

public class SkiaCpuBackendTests
{
    // テストを速くするため、小さなシーン（320×180・30fps）で描く
    private static readonly SceneFormat Small = new(320, 180, 30, 48000);
    private static readonly (byte, byte, byte, byte) Red = (255, 0, 0, 255);
    private static readonly (byte, byte, byte, byte) Blue = (0, 0, 255, 255);

    private readonly TestMedia _media = new();
    private readonly SkiaCpuBackend _backend = new();

    private static readonly Scene Empty = Scene.CreateNew("テスト", Small, 3);

    private static LayerId Layer(Scene scene, int index) => scene.Layers[index].Id;

    private ImageItem Image(BgraImage image, int layer = 0) => Image(new SourceImage(image), layer);

    private ImageItem Image(SourceImage image, int layer = 0) =>
        new(ItemId.New(), Layer(Empty, layer), 0, 30, _media.AddImage(image));

    private BgraImage Render(Item item, RgbaColor? background = null, double scale = 1) =>
        Render([item], background, scale);

    private BgraImage Render(Item[] items, RgbaColor? background = null, double scale = 1, long frame = 0)
    {
        var scene = Empty with { Items = Empty.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };
        return _backend.Render(TimelineEvaluator.Evaluate(scene, frame, background), _media, scale, TestContext.Current.CancellationToken);
    }

    private static void AssertPixel(BgraImage image, int x, int y, (int R, int G, int B, int A) expected, int tolerance = 1) =>
        Pixels.AssertColor(image, x, y, expected, tolerance);

    [Fact]
    public void FillsTheBackground()
    {
        var plan = TimelineEvaluator.Evaluate(Empty, 0, new RgbaColor(10, 20, 30));

        var image = _backend.Render(plan, _media, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((320, 180), (image.Width, image.Height));
        AssertPixel(image, 0, 0, (10, 20, 30, 255), tolerance: 0);
        AssertPixel(image, 319, 179, (10, 20, 30, 255), tolerance: 0);

        var transparent = _backend.Render(TimelineEvaluator.Evaluate(Empty, 0, RgbaColor.Transparent), _media, cancellationToken: TestContext.Current.CancellationToken);
        AssertPixel(transparent, 5, 5, (0, 0, 0, 0), tolerance: 0);
    }

    [Fact]
    public void PlacesAnImageAtTheCenterPixelExactly()
    {
        // 64×48 の画像は、x 128〜191・y 66〜113 に入る
        var image = Render(Image(TestImages.Solid(64, 48, Red)));

        AssertPixel(image, 160, 90, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 128, 66, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 191, 113, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 127, 66, (0, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 192, 90, (0, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 160, 114, (0, 0, 0, 255), tolerance: 0);
    }

    [Fact]
    public void ScalesTheOutputForPreview()
    {
        var image = Render(Image(TestImages.Solid(64, 48, Red)), scale: 0.5);

        Assert.Equal((160, 90), (image.Width, image.Height));
        AssertPixel(image, 64, 33, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 95, 56, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 63, 45, (0, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 96, 45, (0, 0, 0, 255), tolerance: 0);
        Assert.Equal(0.5, _media.LastScale, 6);
    }

    [Fact]
    public void DrawsDownscaledSourcesAtTheirOriginalSize()
    {
        // 64×48 の素材を 32×24 にデコードしたもの
        var image = Render(Image(new SourceImage(TestImages.Solid(32, 24, Red), 64, 48)));

        AssertPixel(image, 128, 66, (255, 0, 0, 255));
        AssertPixel(image, 191, 113, (255, 0, 0, 255));
        AssertPixel(image, 127, 90, (0, 0, 0, 255));
        AssertPixel(image, 192, 90, (0, 0, 0, 255));
    }

    [Fact]
    public void CropsAndKeepsTheVisiblePartCentered()
    {
        var halves = TestImages.Halves(64, 48, Red, Blue);

        // 右半分を削ると、左半分（赤）が中心に来る：x 144〜175
        var left = Render(Image(halves) with { Crop = new Crop(0, 0, 32, 0) });
        AssertPixel(left, 144, 90, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(left, 175, 90, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(left, 143, 90, (0, 0, 0, 255), tolerance: 0);
        AssertPixel(left, 176, 90, (0, 0, 0, 255), tolerance: 0);

        var right = Render(Image(halves) with { Crop = new Crop(32, 10, 0, 10) });
        AssertPixel(right, 144, 90, (0, 0, 255, 255), tolerance: 0);
        AssertPixel(right, 175, 90, (0, 0, 255, 255), tolerance: 0);
        AssertPixel(right, 160, 76, (0, 0, 255, 255), tolerance: 0); // 高さ 28：y 76〜103
        AssertPixel(right, 160, 75, (0, 0, 0, 255), tolerance: 0);

        Assert.Equal(Render(Image(halves) with { Crop = new Crop(40, 0, 40, 0) }).Pixels, Render([]).Pixels);
    }

    [Fact]
    public void RotatesClockwise()
    {
        // 左が赤・右が青の 64×48 を 90 度回すと、上が赤・下が青の 48×64（x 136〜183・y 58〜121）になる
        var item = Image(TestImages.Halves(64, 48, Red, Blue)) with { Transform = Transform2D.Identity with { Rotation = 90.0 } };

        var image = Render(item);

        AssertPixel(image, 160, 60, (255, 0, 0, 255));
        AssertPixel(image, 160, 119, (0, 0, 255, 255));
        AssertPixel(image, 137, 70, (255, 0, 0, 255));
        AssertPixel(image, 182, 110, (0, 0, 255, 255));
        AssertPixel(image, 134, 90, (0, 0, 0, 255));
        AssertPixel(image, 185, 90, (0, 0, 0, 255));
    }

    [Fact]
    public void AppliesOpacityAndPremultipliedAlpha()
    {
        var half = Render(Image(TestImages.Solid(64, 48, (255, 255, 255, 255))) with { Opacity = 0.5 });
        AssertPixel(half, 160, 90, (128, 128, 128, 255));

        // 半透明の赤（straight で (255, 0, 0, 128)）を白の上に重ねる
        var translucent = Render(Image(TestImages.Solid(64, 48, (255, 0, 0, 128))), RgbaColor.White);
        AssertPixel(translucent, 160, 90, (255, 127, 127, 255));

        // 透明な背景の上では、透明度がそのまま残る
        var onTransparent = Render(Image(TestImages.Solid(64, 48, (255, 0, 0, 128))), RgbaColor.Transparent);
        AssertPixel(onTransparent, 160, 90, (128, 0, 0, 128));
        AssertPixel(onTransparent, 10, 10, (0, 0, 0, 0), tolerance: 0);
    }

    [Theory]
    [InlineData(BlendMode.Normal, 60, 120, 180, 60, 120, 180)]
    [InlineData(BlendMode.Add, 50, 100, 150, 250, 200, 200)]
    [InlineData(BlendMode.Add, 100, 200, 250, 255, 255, 255)] // 255 で止まる
    [InlineData(BlendMode.Subtract, 50, 50, 100, 150, 50, 0)] // 0 で止まる
    [InlineData(BlendMode.Multiply, 128, 128, 128, 100, 50, 25)]
    [InlineData(BlendMode.Screen, 100, 100, 100, 222, 161, 130)]
    [InlineData(BlendMode.Overlay, 128, 128, 128, 200, 100, 50)]
    [InlineData(BlendMode.Lighten, 100, 150, 40, 200, 150, 50)]
    [InlineData(BlendMode.Darken, 100, 150, 40, 100, 100, 40)]
    [InlineData(BlendMode.Difference, 50, 150, 100, 150, 50, 50)]
    public void BlendsWithTheLayersBelow(BlendMode mode, byte r, byte g, byte b, int expectedR, int expectedG, int expectedB)
    {
        // 下の色は (200, 100, 50)
        var item = Image(TestImages.Solid(64, 48, (r, g, b, 255))) with { Blend = mode };

        var image = Render(item, new RgbaColor(200, 100, 50));

        AssertPixel(image, 160, 90, (expectedR, expectedG, expectedB, 255), tolerance: 2);
        AssertPixel(image, 10, 10, (200, 100, 50, 255), tolerance: 0);
    }

    [Fact]
    public void SubtractRespectsOpacity()
    {
        var item = Image(TestImages.Solid(64, 48, (100, 100, 100, 255))) with { Blend = BlendMode.Subtract, Opacity = 0.5 };

        AssertPixel(Render(item, new RgbaColor(200, 100, 50)), 160, 90, (150, 50, 0, 255), tolerance: 2);
    }

    [Fact]
    public void AsksForTheVideoFrameAtTheSourceTime()
    {
        // 素材の 0.5 秒より前は赤、後は青の動画を、素材の 1/3 秒から 2 倍速で使う
        var asset = _media.AddVideo(time => TestImages.Solid(64, 48, time < new Rational(1, 2) ? Red : Blue));
        var item = new VideoItem(ItemId.New(), Layer(Empty, 0), 0, 30, asset)
        {
            Playback = new MediaPlayback(new Rational(1, 3), 2),
        };

        // 2 フレーム目は素材の 1/3 + 2/30 × 2 = 7/15 秒、3 フレーム目は 8/15 秒
        AssertPixel(Render([item], frame: 2), 160, 90, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(Render([item], frame: 3), 160, 90, (0, 0, 255, 255), tolerance: 0);
    }

    [Fact]
    public void StacksTachieLayersFromTheBack()
    {
        // 奥は 40×40 の赤、手前は 20×20 の青（左上をそろえる）。立ち絵の大きさは 40×40 で、中心に置く（x・y は 140〜179・70〜109）
        var body = _media.AddFile("立ち絵/体/body.png", TestImages.Solid(40, 40, Red));
        var eyes = _media.AddFile("立ち絵/目/e.png", TestImages.Solid(20, 20, Blue));
        var missing = "立ち絵/他/missing.png";
        var node = new DrawNode(null, new TachieContent([body, eyes, missing]), Crop.None, TimelineEvaluator.Transform(Transform2D.Identity, 0, Small), 1, BlendMode.Normal);

        var image = _backend.Render(new FramePlan(320, 180, RgbaColor.Black, [node]), _media, cancellationToken: TestContext.Current.CancellationToken);

        AssertPixel(image, 145, 75, (0, 0, 255, 255), tolerance: 0);
        AssertPixel(image, 170, 100, (255, 0, 0, 255), tolerance: 0);
        AssertPixel(image, 139, 75, (0, 0, 0, 255), tolerance: 0);
    }

    [Fact]
    public void SkipsMissingMedia()
    {
        var missing = new ImageItem(ItemId.New(), Layer(Empty, 0), 0, 30, AssetId.New());

        Assert.Equal(Render([]).Pixels, Render(missing).Pixels);
    }

    [Fact]
    public void RejectsInvalidScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _backend.Render(TimelineEvaluator.Evaluate(Empty, 0), _media, 0, TestContext.Current.CancellationToken));
    }

    // 変形・反転・切り抜き・不透明度・合成モードを組み合わせたシーン
    [Fact]
    public void MeasuresContentAfterCropping()
    {
        Fonts.RequireOrSkip();
        var measurer = new ContentMeasurer();
        // 縮小して読んだ画像でも、元の大きさで測る
        var image = _media.AddImage(new SourceImage(new BgraImage(10, 5, new byte[200]), 400, 200));
        var shape = new ShapeStyle(ShapeKind.Rectangle, 120, 80, new SolidPaint(RgbaColor.White));

        DrawNode Node(DrawContent content, Crop? crop = null) => new(null, content, crop ?? Crop.None, System.Numerics.Matrix3x2.Identity, 1, BlendMode.Normal);

        Assert.Equal((400f, 200f), measurer.Measure(Node(new ImageContent(image), new Crop(10, 20, 30, 40)), _media, 1920, 1080) is var (w, h) ? (w + 40, h + 60) : default);
        Assert.Equal((120f, 80f), measurer.Measure(Node(new ShapeContent(shape)), _media, 1920, 1080));
        Assert.Equal((1920f, 1080f), measurer.Measure(Node(new ShapeContent(shape with { Kind = ShapeKind.Background })), _media, 1920, 1080));
        Assert.True(measurer.Measure(Node(new TextContent("あいう", TextStyle.Default)), _media, 1920, 1080) is ({ } textWidth, { } textHeight) && textWidth > 100 && textHeight > 50);
        Assert.Null(measurer.Measure(Node(new ImageContent(AssetId.New())), _media, 1920, 1080));
    }
}
