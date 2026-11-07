using System.Collections.Immutable;
using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Rendering.Transitions;

namespace NakkuntMovieMaker.Rendering.Tests;

/// <summary>合成の色空間（sRGB・リニア）による違い</summary>
public sealed class LinearCompositingTests
{
    private readonly SkiaCpuBackend _backend = new();

    [Theory]
    [InlineData(CompositingSpace.Srgb, 128)]
    [InlineData(CompositingSpace.Linear, 188)]
    public void HalfCrossfadeFromBlackToWhite(CompositingSpace space, int expected)
    {
        // 黒から白への 50% のクロスフェード。sRGB では値の真ん中（128）、リニアでは光の量の真ん中（sRGB で約 188）になる
        var plan = Plan(space, new TransitionNode(BuiltInTransitions.Crossfade.Type, 0.5, Empty, Square(RgbaColor.Black), Square(RgbaColor.White)));

        var image = _backend.Render(plan, new TestMedia(), 1, TestContext.Current.CancellationToken);

        var (b, g, r, a) = Pixel(image, 8, 8);
        Assert.InRange(r, expected - 1, expected + 1);
        Assert.Equal((r, r, 255), (g, b, a));
    }

    [Theory]
    [InlineData(CompositingSpace.Srgb)]
    [InlineData(CompositingSpace.Linear)]
    public void OpaqueColorsStayTheSame(CompositingSpace space)
    {
        // 不透明な色をそのまま描くだけなら、どちらでも同じ値になる（sRGB → リニア → sRGB で戻る）
        var color = new RgbaColor(200, 120, 30);
        var image = _backend.Render(Plan(space, Square(color)), new TestMedia(), 1, TestContext.Current.CancellationToken);

        var (b, g, r, a) = Pixel(image, 8, 8);
        Assert.Equal((200, 120, 30, 255), (r, g, b, a));
    }

    [Fact]
    public void LinearPlansAreDifferentCacheKeys()
    {
        var node = Square(RgbaColor.White);
        Assert.NotEqual(Plan(CompositingSpace.Srgb, node), Plan(CompositingSpace.Linear, node));
        Assert.Equal(Plan(CompositingSpace.Linear, node), Plan(CompositingSpace.Linear, node));
    }

    private static readonly EffectParameters Empty = new(ImmutableSortedDictionary<string, object>.Empty);

    private static FramePlan Plan(CompositingSpace space, params RenderNode[] nodes) =>
        new(16, 16, RgbaColor.Black, new EquatableArray<RenderNode>([.. nodes])) { Color = new FrameColor(space) };

    // 出力の全体を覆う、その色の四角形
    private static DrawNode Square(RgbaColor color) =>
        new(null, new ShapeContent(new ShapeStyle(ShapeKind.Rectangle, 32, 32, new SolidPaint(color))), Crop.None, Matrix3x2.CreateTranslation(8, 8), 1, BlendMode.Normal);

    private static (int B, int G, int R, int A) Pixel(BgraImage image, int x, int y)
    {
        var i = y * image.Stride + x * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
    }
}
