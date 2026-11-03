using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

public class ThumbnailExtractorTests
{
    // 画像の中央付近の色が、どの色に近いかを調べる
    private static (byte B, byte G, byte R, byte A) Center(BgraImage image) => image.GetPixel(image.Width / 2, image.Height / 2);

    [Fact]
    public void ExtractsTheFrameAtTheRequestedTime()
    {
        Ffmpeg.RequireOrSkip();

        // 0〜1 秒は赤、1〜2 秒は青
        var images = ThumbnailExtractor.ExtractMany(
            Ffmpeg.Fixture("red-blue-2s.mp4"), [new Rational(1, 2), new Rational(3, 2), Rational.Zero], 160, 160);

        var (b, g, r, a) = Center(images[0]);
        Assert.True(r > 200 && g < 60 && b < 60, $"0.5 秒は赤のはず: R{r} G{g} B{b}");
        (b, g, r, a) = Center(images[1]);
        Assert.True(b > 200 && r < 60 && g < 60, $"1.5 秒は青のはず: R{r} G{g} B{b}");
        Assert.Equal(255, a);
        (b, g, r, _) = Center(images[2]);
        Assert.True(r > 200 && b < 60, $"0 秒は赤のはず: R{r} G{g} B{b}");
    }

    [Fact]
    public void KeepsAspectRatioWithinTheBox()
    {
        Ffmpeg.RequireOrSkip();

        var wide = ThumbnailExtractor.Extract(Ffmpeg.Fixture("red-blue-2s.mp4"), Rational.Zero, 160, 160);
        var tall = ThumbnailExtractor.Extract(Ffmpeg.Fixture("testsrc-1s.webm"), Rational.Zero, 1000, 90);

        Assert.Equal((160, 120), (wide.Width, wide.Height));
        Assert.Equal((160, 90), (tall.Width, tall.Height));
        Assert.Equal(wide.Width * wide.Height * 4, wide.Pixels.Length);
    }

    [Fact]
    public void DoesNotEnlargeSmallImages()
    {
        Ffmpeg.RequireOrSkip();

        var image = ThumbnailExtractor.Extract(Ffmpeg.Fixture("green-64x48.png"), Rational.Zero, 640, 480);

        Assert.Equal((64, 48), (image.Width, image.Height));
        var (b, g, r, _) = Center(image);
        Assert.True(g > 100 && r < 30 && b < 30, $"緑のはず: R{r} G{g} B{b}");
    }

    [Fact]
    public void TimesPastTheEndReturnTheLastFrame()
    {
        Ffmpeg.RequireOrSkip();

        var image = ThumbnailExtractor.Extract(Ffmpeg.Fixture("red-blue-2s.mp4"), new Rational(10, 1), 160, 160);

        var (b, _, r, _) = Center(image);
        Assert.True(b > 200 && r < 60, $"最後のフレーム（青）のはず: R{r} B{b}");
    }

    [Fact]
    public void RejectsFilesWithoutVideo()
    {
        Ffmpeg.RequireOrSkip();

        Assert.Throws<MediaException>(() => ThumbnailExtractor.Extract(Ffmpeg.Fixture("tone-1s.wav"), Rational.Zero, 160, 90));
        Assert.Throws<MediaException>(() => ThumbnailExtractor.Extract(Ffmpeg.Fixture("not-media.txt"), Rational.Zero, 160, 90));
    }
}
