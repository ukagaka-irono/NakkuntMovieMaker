namespace NakkuntMovieMaker.Media.Tests;

public class ImageCacheTests
{
    [Fact]
    public void StoresTheScaledImageAndReusesIt()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        var image = cache.GetImage(Ffmpeg.Fixture("yellow-64x48.jpg"), 32, 32);

        Assert.Equal((32, 24), (image.Width, image.Height));
        var (b, g, r, a) = image.GetPixel(16, 12);
        Assert.True(r > 230 && g > 230 && b < 40 && a == 255, $"黄色のはず: R{r} G{g} B{b} A{a}");

        var file = Assert.Single(folder.AllFiles());
        var written = File.GetLastWriteTimeUtc(file);
        var again = cache.GetImage(Ffmpeg.Fixture("yellow-64x48.jpg"), 32, 32);
        Assert.Equal(image.Pixels, again.Pixels);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Assert.Single(folder.AllFiles())));
    }

    [Fact]
    public void KeepsOneCacheFilePerSize()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        var small = cache.GetImage(Ffmpeg.Fixture("green-64x48.png"), 16, 16);
        var full = cache.GetImage(Ffmpeg.Fixture("green-64x48.png"), 640, 480);

        Assert.Equal((16, 12), (small.Width, small.Height));
        Assert.Equal((64, 48), (full.Width, full.Height)); // 拡大はしない
        Assert.Equal(2, folder.AllFiles().Length);
    }

    [Fact]
    public void RejectsFilesThatAreNotImages()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        Assert.Throws<MediaException>(() => cache.GetImage(Ffmpeg.Fixture("tone-1s.wav"), 32, 32));
        Assert.Throws<MediaException>(() => cache.GetImage(Ffmpeg.Fixture("missing.png"), 32, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetImage(Ffmpeg.Fixture("green-64x48.png"), 0, 32));
        Assert.Empty(folder.AllFiles());
    }
}
