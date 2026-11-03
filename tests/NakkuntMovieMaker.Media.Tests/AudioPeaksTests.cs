namespace NakkuntMovieMaker.Media.Tests;

public class AudioPeaksTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SummarizesTheWaveform()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();

        var peaks = new MediaCache(folder.Path).GetPeaks(Ffmpeg.Fixture("tone-1s.wav"), Token);

        Assert.Equal((48000, 1, 48000L), (peaks.SampleRate, peaks.Channels, peaks.FrameCount));
        Assert.Equal((48000 + AudioPeaks.SamplesPerPeak - 1) / AudioPeaks.SamplesPerPeak, peaks.PeakCount);
        var (min, max) = peaks.GetRange(0, 0, 48000);
        Assert.InRange(min, -0.13f, -0.12f);
        Assert.InRange(max, 0.12f, 0.13f);
    }

    [Fact]
    public void ShowsSilenceWhereThereIsNoSound()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();

        // 0.5 秒までは無音
        var peaks = new MediaCache(folder.Path).GetPeaks(Ffmpeg.Fixture("late-audio.mkv"), Token);

        var (min, max) = peaks.GetRange(0, 0, 23000);
        Assert.InRange(min, -0.001f, 0);
        Assert.InRange(max, 0, 0.001f);
        Assert.True(peaks.GetRange(0, 24000, 48000).Max > 0.1f);
        // 範囲の外
        Assert.Equal((0f, 0f), peaks.GetRange(0, 50000, 60000));
        Assert.Equal((0f, 0f), peaks.GetRange(0, -1000, -10));
        Assert.Throws<ArgumentOutOfRangeException>(() => peaks.GetRange(1, 0, 100));
    }

    [Fact]
    public void SavesAndLoadsTheSameValues()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);
        var peaks = cache.GetPeaks(Ffmpeg.Fixture("tone-1s.mp3"), Token);

        using var stream = new MemoryStream();
        peaks.Save(stream);
        stream.Position = 0;
        var loaded = AudioPeaks.Load(stream);

        Assert.Equal((peaks.Channels, peaks.FrameCount, peaks.PeakCount), (loaded.Channels, loaded.FrameCount, loaded.PeakCount));
        for (var start = 0L; start < peaks.FrameCount; start += 1000)
        {
            Assert.Equal(peaks.GetRange(1, start, start + 1000), loaded.GetRange(1, start, start + 1000));
        }

        // 2 回目はキャッシュのファイルから読む（PCM とピークの 2 つ）
        Assert.Equal(peaks.GetRange(0, 0, 48000), cache.GetPeaks(Ffmpeg.Fixture("tone-1s.mp3"), Token).GetRange(0, 0, 48000));
        Assert.Equal(2, folder.AllFiles().Length);
    }

    [Fact]
    public void RejectsBrokenFiles()
    {
        Assert.Throws<InvalidDataException>(() => AudioPeaks.Load(new MemoryStream([1, 2, 3])));
        Assert.Throws<InvalidDataException>(() => AudioPeaks.Load(new MemoryStream(new byte[64])));
    }
}
