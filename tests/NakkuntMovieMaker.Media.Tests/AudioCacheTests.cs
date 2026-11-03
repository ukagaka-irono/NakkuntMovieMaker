using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

public class AudioCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // 1 チャンネル目の [start, end) の絶対値の最大
    private static float MaxAbs(PcmAudio audio, long start, long end)
    {
        var samples = audio.GetSamples(start, (int)(end - start));
        var max = 0f;
        for (var i = 0; i < samples.Length; i += audio.Channels)
        {
            max = Math.Max(max, Math.Abs(samples[i]));
        }

        return max;
    }

    // 1 チャンネル目で、負から正に変わる回数（正弦波なら周波数 × 秒数）
    private static int RisingZeroCrossings(PcmAudio audio)
    {
        var samples = audio.GetSamples(0, (int)audio.FrameCount);
        var count = 0;
        for (var i = audio.Channels; i < samples.Length; i += audio.Channels)
        {
            if (samples[i - audio.Channels] < 0 && samples[i] >= 0)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void ConvertsWavWithoutChangingTheSampleCount()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        using var audio = cache.OpenAudio(Ffmpeg.Fixture("tone-1s.wav"), Token);

        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(1, audio.Channels); // モノラルはモノラルのまま
        Assert.Equal(48000, audio.FrameCount);
        Assert.Equal(Rational.One, audio.Duration);
        // lavfi の正弦波の振幅は 1/8
        Assert.InRange(MaxAbs(audio, 0, audio.FrameCount), 0.12f, 0.13f);
        Assert.InRange(RisingZeroCrossings(audio), 999, 1001);
    }

    [Theory]
    [InlineData("tone-1s.mp3", 48000)] // 44.1kHz から変換する。MP3 の先頭の遅延は FFmpeg が取り除く
    [InlineData("red-blue-2s.mp4", 96000)] // AAC の先頭の遅延（プライミング）は MP4 の編集リストで取り除く
    public void ResampledLengthMatchesTheSourceDuration(string fixture, long expectedFrames)
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        using var audio = cache.OpenAudio(Ffmpeg.Fixture(fixture), Token);

        Assert.Equal(2, audio.Channels);
        Assert.InRange(audio.FrameCount, expectedFrames - 1, expectedFrames + 1);
    }

    [Fact]
    public void KeepsThePitchWhenResampling()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        using var audio = cache.OpenAudio(Ffmpeg.Fixture("tone-1s.mp3"), Token);

        // 1000Hz の音が 1 秒。変換で高さが変わっていなければ 1000 回
        Assert.InRange(RisingZeroCrossings(audio), 997, 1003);
        // 作るときにモノラルからステレオにしたので、振幅は 1/8 の −3dB（約 0.088）
        Assert.InRange(MaxAbs(audio, 4800, 43200), 0.075f, 0.095f);
    }

    [Fact]
    public void PadsSilenceWhenTheAudioStartsAfterTheVideo()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        // 映像は 0 秒から、音声は 0.5 秒から 0.5 秒間
        using var audio = cache.OpenAudio(Ffmpeg.Fixture("late-audio.mkv"), Token);

        Assert.Equal(48000, audio.FrameCount);
        Assert.True(MaxAbs(audio, 0, 23990) < 1e-4f, "0.5 秒までは無音のはず");
        Assert.True(MaxAbs(audio, 24010, 48000) > 0.1f, "0.5 秒からは音があるはず");
    }

    [Fact]
    public void ReadFillsSilenceOutsideTheAudio()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        using var audio = new MediaCache(folder.Path).OpenAudio(Ffmpeg.Fixture("tone-1s.mp3"), Token);
        var buffer = new float[20 * 2];

        audio.Read(-10, buffer);
        Assert.All(buffer[..20], sample => Assert.Equal(0f, sample));
        Assert.Equal(audio.GetSamples(0, 10).ToArray(), buffer[20..]);

        audio.Read(audio.FrameCount - 5, buffer);
        Assert.Equal(audio.GetSamples(audio.FrameCount - 5, 5).ToArray(), buffer[..10]);
        Assert.All(buffer[10..], sample => Assert.Equal(0f, sample));

        // すべて範囲の外（前・後ろ）なら無音
        Array.Fill(buffer, 1f);
        audio.Read(-1000, buffer);
        Assert.All(buffer, sample => Assert.Equal(0f, sample));
        Array.Fill(buffer, 1f);
        audio.Read(audio.FrameCount + 1000, buffer);
        Assert.All(buffer, sample => Assert.Equal(0f, sample));

        Assert.Throws<ArgumentException>(() => audio.Read(0, new float[3]));
    }

    [Fact]
    public void ReusesTheCacheUntilTheSourceChanges()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var source = folder.Combine("tone.wav");
        File.Copy(Ffmpeg.Fixture("tone-1s.wav"), source);
        var cache = new MediaCache(folder.Combine("cache"));

        using (cache.OpenAudio(source, Token))
        {
        }

        var first = Assert.Single(Directory.GetFiles(cache.Directory, "*.pcm", SearchOption.AllDirectories));
        var written = File.GetLastWriteTimeUtc(first);
        using (cache.OpenAudio(source, Token))
        {
        }

        Assert.Equal(written, File.GetLastWriteTimeUtc(Assert.Single(Directory.GetFiles(cache.Directory, "*.pcm", SearchOption.AllDirectories))));

        // 素材を差し替えた（更新日時が変わった）ら作り直す
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        using (cache.OpenAudio(source, Token))
        {
        }

        Assert.Equal(2, Directory.GetFiles(cache.Directory, "*.pcm", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void ConcurrentRequestsShareOneCacheFile()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        var counts = new long[8];
        Parallel.For(0, counts.Length, i =>
        {
            using var audio = cache.OpenAudio(Ffmpeg.Fixture("tone-1s.wav"), Token);
            counts[i] = audio.FrameCount;
        });

        Assert.All(counts, count => Assert.Equal(48000, count));
        Assert.Single(folder.AllFiles());
    }

    [Fact]
    public void RebuildsABrokenCacheFile()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);
        using (cache.OpenAudio(Ffmpeg.Fixture("tone-1s.wav"), Token))
        {
        }

        File.WriteAllText(Assert.Single(folder.AllFiles()), "壊れたキャッシュ");

        using var audio = cache.OpenAudio(Ffmpeg.Fixture("tone-1s.wav"), Token);
        Assert.Equal(48000, audio.FrameCount);
    }

    [Fact]
    public void CancellingLeavesNoFiles()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        Assert.ThrowsAny<OperationCanceledException>(() => cache.OpenAudio(Ffmpeg.Fixture("tone-1s.mp3"), new CancellationToken(canceled: true)));
        Assert.Empty(folder.AllFiles());
    }

    [Fact]
    public void RejectsFilesWithoutAudio()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);

        Assert.Throws<MediaException>(() => cache.OpenAudio(Ffmpeg.Fixture("green-64x48.png"), Token));
        Assert.Throws<MediaException>(() => cache.OpenAudio(Ffmpeg.Fixture("not-media.txt"), Token));
        Assert.Throws<MediaException>(() => cache.OpenAudio(Ffmpeg.Fixture("missing.wav"), Token));
        Assert.Empty(folder.AllFiles());
    }
}
