using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Text;

namespace NakkuntMovieMaker.Rendering.Tests;

public class TextRenderingTests
{
    private static readonly SceneFormat Small = new(320, 180, 30, 48000);
    private static readonly Scene Empty = Scene.CreateNew("テスト", Small, 3);
    private static readonly RgbaColor Background = new(30, 40, 60);

    private readonly SkiaCpuBackend _backend = new();
    private readonly TestMedia _media = new();

    private static TextStyle Style(double size = 40) => new("", size, RgbaColor.White);

    private static TextItem Text(string text, TextStyle style, int layer = 0, Transform2D? transform = null) =>
        new(ItemId.New(), Empty.Layers[layer].Id, 0, 90, text) { Style = style, Transform = transform ?? Transform2D.Identity };

    private BgraImage Render(long frame = 0, params Item[] items)
    {
        var scene = Empty with { Items = Empty.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };
        return _backend.Render(TimelineEvaluator.Evaluate(scene, frame, Background), _media, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void LaysOutLinesWithTheLineHeight()
    {
        Fonts.RequireOrSkip();

        var one = TextLayout.Create("あいう", Style(50));
        var two = TextLayout.Create("あ\nあいう", Style(50));

        Assert.Equal(60, one.Height, 3); // 行の高さ = 50 × 1.2
        Assert.InRange(one.Width, 100, 151);
        Assert.Equal(120, two.Height, 3);
        Assert.Equal(one.Width, two.Width, 3); // 一番長い行の幅
        Assert.Equal(5, two.TextElementCount); // 改行も 1 文字
    }

    [Fact]
    public void AddsLetterSpacingBetweenCharacters()
    {
        Fonts.RequireOrSkip();

        var normal = TextLayout.Create("あいう", Style(50));
        var spaced = TextLayout.Create("あいう", Style(50) with { LetterSpacing = 10 });

        Assert.Equal(normal.Width + 20, spaced.Width, 3);
    }

    [Fact]
    public void LaysOutVerticalTextInColumnsFromTheRight()
    {
        Fonts.RequireOrSkip();

        var vertical = Style(50) with { Vertical = true };
        var layout = TextLayout.Create("あいう\nえお", vertical);

        Assert.Equal(120, layout.Width, 3); // 2 列 × 行の高さ
        // 一番長い列の長さ（このフォントは縦の字送りもプロポーショナルなので、1em より少し短いことがある）
        Assert.Equal(TextLayout.Create("あいう", vertical).Height, layout.Height, 3);
        Assert.InRange(layout.Height, 120, 151);
    }

    [Fact]
    public void CountsCombinedCharactersAsOne()
    {
        Fonts.RequireOrSkip();

        Assert.Equal(6, TextLayout.Create("こんにちは👋", Style()).TextElementCount);
        Assert.Equal(0, TextLayout.Create("", Style()).TextElementCount);
    }

    [Fact]
    public void RevealsCharactersWithoutMovingThem()
    {
        Fonts.RequireOrSkip();
        var style = Style(40) with { RevealCharsPerSecond = 10, Alignment = TextAlignment.Left };
        var item = Text("あいうえお", style);

        var none = Render(0, item);
        var two = Render(6, item); // 0.2 秒で 2 文字
        var all = Render(60, item);

        Assert.Equal(0, Pixels.CountDifferent(none, (30, 40, 60)));
        var shown = Pixels.CountDifferent(two, (30, 40, 60));
        Assert.InRange(shown, 1, Pixels.CountDifferent(all, (30, 40, 60)) - 1);

        // 最初の 2 文字は、全部を表示したときと同じ位置に描かれる
        for (var i = 0; i < two.Pixels.Length; i += 4)
        {
            if (two.Pixels[i] != 60 || two.Pixels[i + 1] != 40 || two.Pixels[i + 2] != 30)
            {
                Assert.Equal(all.Pixels.AsSpan(i, 4).ToArray(), two.Pixels.AsSpan(i, 4).ToArray());
            }
        }
    }

    [Fact]
    public void DrawsTheBackgroundBoxAroundTheText()
    {
        Fonts.RequireOrSkip();
        var style = Style(40) with { Background = new TextBackground(RgbaColor.Black, Padding: 10) };
        var layout = TextLayout.Create("あ", style);

        var image = Render(0, Text("あ", style));

        // 箱（中心に置かれる）の左上から padding だけ外側までが背景
        var left = (int)Math.Ceiling(160 - layout.Width / 2 - 10) + 1;
        var top = (int)Math.Ceiling(90 - layout.Height / 2 - 10) + 1;
        Pixels.AssertColor(image, left, top, (0, 0, 0, 255), tolerance: 0);
        Pixels.AssertColor(image, left - 3, top, (30, 40, 60, 255), tolerance: 0);
    }
}
