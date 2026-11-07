using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

public class ProxyTests(ITestOutputHelper output)
{
    // プロキシと、素材を同じ大きさに縮小したものの PSNR の下限（どちらも 8 ビットの YUV を経るので、それほど高くはならない）
    private const double MinPsnr = 35;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<EncoderPreference> Encoders => [EncoderPreference.Software, EncoderPreference.Auto];

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(7680, 4320, 1920, 1080)] // 1/2 でも 1080p を超えるので、1080p に収める
    [InlineData(2160, 3840, 1080, 1920)] // 縦長
    [InlineData(4096, 2160, 1920, 1012)]
    [InlineData(1366, 768, 682, 384)] // 683 は奇数なので切り下げる
    [InlineData(128, 96, 64, 48)]
    [InlineData(3, 3, 2, 2)]
    public void ChoosesHalfSizeCappedAt1080p(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ProxySettings.Default.TargetSize(width, height));
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void MatchesTheSourceFrameAtEveryTimeInAVariableFrameRateVideo(EncoderPreference encoder)
    {
        // 0〜29 は 30fps、0.5 秒あいて、30〜59 が 1.5 秒から（B フレームあり）
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var source = Ffmpeg.Fixture("frames-vfr.mp4");
        var proxy = new MediaCache(folder.Path).GetProxy(source, new ProxySettings(Encoder: encoder), cancellationToken: Token);

        var times = new List<Rational>();
        for (var k = 0; k < 30; k++)
        {
            times.Add(new Rational(k, 30));
            times.Add(new Rational(k, 30) + new Rational(1, 60));
            times.Add(new Rational(45 + k, 30));
            times.Add(new Rational(45 + k, 30) + new Rational(1, 60));
        }

        times.AddRange([new Rational(12, 10), new Rational(14, 10), new Rational(-1, 1), new Rational(100, 1)]);
        var matched = CompareFrames(source, proxy, times);

        Assert.Equal(60, matched.Distinct().Count());
        Assert.Equal((64, 48), Probe(proxy).Size);
    }

    [Fact]
    public void MatchesTheSourceFrameInAConstantFrameRateVideoWithBFrames()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var source = Ffmpeg.Fixture("frames-2997.mp4");
        var proxy = new MediaCache(folder.Path).GetProxy(source, new ProxySettings(Encoder: EncoderPreference.Software), cancellationToken: Token);

        var times = Enumerable.Range(0, 60).Select(k => TimeMath.FrameToSeconds(k, Rational.Fps29_97) + new Rational(1, 100)).ToList();
        var matched = CompareFrames(source, proxy, times);

        Assert.Equal(Enumerable.Range(0, 60), matched);
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void UsesShortGopsWithoutBFrames(EncoderPreference encoder)
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var proxyPath = folder.Combine("proxy.mp4");

        var info = ProxyGenerator.Generate(Ffmpeg.Fixture("frames-2997.mp4"), proxyPath, new ProxySettings(Encoder: encoder), cancellationToken: Token);
        output.WriteLine($"エンコーダー: {info.Encoder}");

        var probe = Probe(proxyPath);
        Assert.Equal("h264", probe.Codec);
        Assert.Equal(60, info.Frames);
        Assert.Equal(60, probe.Packets);
        Assert.True(probe.DecodeOrderIsDisplayOrder, "B フレームがある（デコードの順と表示の順が違う）");
        Assert.True(probe.MaxKeyframeDistance <= 15, $"キーフレームの間隔が {probe.MaxKeyframeDistance} フレーム");
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void CopiesBt709Tags(EncoderPreference encoder)
    {
        // この素材は、変換式（BT.709）と範囲（リミテッド）だけタグがあり、原色と伝達特性は「指定なし」。それもそのまま写す
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var source = Ffmpeg.Fixture("color-c83c28-bt709.mp4");
        var proxyPath = folder.Combine("proxy.mp4");
        Assert.Equal(new VideoColorTags(VideoColorTags.Unspecified, VideoColorTags.Unspecified, 1, false), Probe(source).Color);

        ProxyGenerator.Generate(source, proxyPath, new ProxySettings(Encoder: encoder), cancellationToken: Token);

        var probe = Probe(proxyPath);
        Assert.Equal((64, 48), probe.Size);
        Assert.Equal(Probe(source).Color, probe.Color);
        AssertColor(proxyPath, 200, 60, 40);
    }

    // 原色・伝達特性・変換式（H.273 の番号）・フルレンジ
    public static TheoryData<EncoderPreference, int, int, int, bool> TagSets => new()
    {
        { EncoderPreference.Software, 5, 6, 6, true }, // BT.601（PAL）・フルレンジ
        { EncoderPreference.Auto, 5, 6, 6, true },
        { EncoderPreference.Software, 1, 1, 1, false }, // BT.709
        { EncoderPreference.Software, 9, 16, 9, false }, // BT.2020・PQ
        { EncoderPreference.Auto, 9, 18, 9, false }, // BT.2020・HLG
    };

    [Theory]
    [MemberData(nameof(TagSets))]
    public void CopiesAllColorTags(EncoderPreference encoder, int primaries, int transfer, int matrix, bool fullRange)
    {
        // タグを付けた素材を作り、プロキシに同じタグが付くかを確かめる
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var sourcePath = folder.Combine("tagged.mp4");
        var tags = new VideoColorTags(primaries, transfer, matrix, fullRange);
        using (var writer = MediaWriter.Create(sourcePath, new VideoEncoderSettings(VideoCodecKind.H264, 160, 120, 30, 2000, EncoderPreference.Software, tags), null))
        {
            var pixels = new byte[160 * 120 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)40, (byte)60, (byte)200, (byte)255);
            }

            for (var k = 0; k < 5; k++)
            {
                writer.WriteVideo(new BgraImage(160, 120, pixels));
            }

            writer.Finish();
        }

        Assert.Equal(tags, Probe(sourcePath).Color);
        var proxyPath = folder.Combine("proxy.mp4");

        var info = ProxyGenerator.Generate(sourcePath, proxyPath, new ProxySettings(Encoder: encoder), cancellationToken: Token);

        output.WriteLine($"エンコーダー: {info.Encoder}");
        Assert.Equal((80, 60), Probe(proxyPath).Size);
        Assert.Equal(tags, Probe(proxyPath).Color);
        AssertColor(proxyPath, 200, 60, 40);
    }

    [Fact]
    public void TagsTheMatrixTheDecoderUsedWhenTheSourceHasNone()
    {
        // タグのない SD の素材は BT.601 で RGB にしているので、プロキシにも BT.601 の変換式を付ける
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var source = Ffmpeg.Fixture("frames-vfr.mp4");
        var proxyPath = folder.Combine("proxy.mp4");
        Assert.Equal(VideoColorTags.Unspecified, Probe(source).Color.Matrix);

        var info = ProxyGenerator.Generate(source, proxyPath, new ProxySettings(Encoder: EncoderPreference.Software), cancellationToken: Token);

        Assert.Equal(6, info.Color.Matrix);
        Assert.Equal(6, Probe(proxyPath).Color.Matrix);
    }

    [Fact]
    public void ReusesTheCachedProxyAndFindsIt()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);
        var source = Ffmpeg.Fixture("frames-vfr.mp4");
        var settings = new ProxySettings(Encoder: EncoderPreference.Software);
        Assert.Null(cache.FindProxy(source, settings));
        Assert.Null(cache.FindProxy(folder.Combine("missing.mp4"), settings));

        var progress = new List<double>();
        var proxy = cache.GetProxy(source, settings, new SyncProgress(progress.Add), Token);

        Assert.Equal(proxy, cache.FindProxy(source, settings));
        Assert.StartsWith(Path.Combine(folder.Path, "proxies"), proxy);
        Assert.Null(cache.FindProxy(source, settings with { Divisor = 4 }));
        Assert.Equal(1, progress[^1]);
        Assert.True(progress.Count > 10);
        Assert.Equal(progress.Order(), progress);

        var written = File.GetLastWriteTimeUtc(proxy);
        Assert.Equal(proxy, cache.GetProxy(source, settings, cancellationToken: Token));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Assert.Single(folder.AllFiles())));
    }

    [Fact]
    public void CancelingLeavesNoPartialFile()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);
        var source = Ffmpeg.Fixture("frames-long.mp4");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var reports = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => cache.GetProxy(
            source,
            new ProxySettings(Encoder: EncoderPreference.Software),
            new SyncProgress(value =>
            {
                if (value > 0.2)
                {
                    cancellation.Cancel();
                }

                reports++;
            }),
            cancellation.Token));

        Assert.True(reports > 1, "書き始める前にキャンセルした");
        Assert.Empty(folder.AllFiles());
        Assert.Null(cache.FindProxy(source));
    }

    [Fact]
    public async Task QueueBuildsProxiesInTheBackground()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);
        using var queue = new ProxyQueue(cache, new ProxySettings(Encoder: EncoderPreference.Software));
        var source = Ffmpeg.Fixture("frames-vfr.mp4");
        var states = new List<ProxyJobState>();
        queue.JobChanged += (_, job) =>
        {
            lock (states)
            {
                states.Add(job.State);
            }
        };

        var job = queue.Enqueue(source);
        Assert.Same(job, queue.Enqueue(source)); // 同じ素材は 1 つの仕事にまとめる
        var path = await job.Completion.WaitAsync(TimeSpan.FromMinutes(1), Token);

        Assert.Equal(ProxyJobState.Completed, job.State);
        Assert.Equal(path, job.ProxyPath);
        Assert.Equal(1, job.Progress);
        Assert.Equal(path, queue.FindProxy(source));
        Assert.Empty(queue.ActiveJobs);
        lock (states)
        {
            Assert.Contains(ProxyJobState.Running, states);
            Assert.Equal(ProxyJobState.Completed, states[^1]);
        }
    }

    [Fact]
    public async Task QueueCancelsWithoutLeavingAFile()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        var cache = new MediaCache(folder.Path);
        using var queue = new ProxyQueue(cache, new ProxySettings(Encoder: EncoderPreference.Software));
        var source = Ffmpeg.Fixture("frames-long.mp4");
        queue.JobChanged += (_, job) =>
        {
            if (job.Progress > 0.1)
            {
                job.Cancel();
            }
        };

        var job = queue.Enqueue(source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(ProxyJobState.Canceled, job.State);
        Assert.Null(queue.FindProxy(source));
        Assert.Empty(folder.AllFiles());
    }

    [Fact]
    public async Task QueueReportsFailures()
    {
        Ffmpeg.RequireOrSkip();
        using var folder = new TempFolder();
        using var queue = new ProxyQueue(new MediaCache(folder.Path));

        var job = queue.Enqueue(Ffmpeg.Fixture("not-media.txt"));

        await Assert.ThrowsAsync<MediaException>(() => job.Completion.WaitAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(ProxyJobState.Failed, job.State);
        Assert.IsType<MediaException>(job.Error);
        Assert.Empty(folder.AllFiles());
    }

    // 時刻ごとに、素材（プロキシの大きさに縮小）とプロキシのフレームを比べる。一致したフレームの番号を返す
    private List<int> CompareFrames(string sourcePath, string proxyPath, IEnumerable<Rational> times)
    {
        using var proxy = VideoDecoder.Open(proxyPath, new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));
        using var source = VideoDecoder.Open(sourcePath, new VideoDecoderOptions(proxy.OutputWidth, proxy.OutputHeight, HardwareDecoding.Disabled));
        Assert.Equal((source.OutputWidth, source.OutputHeight), (proxy.OutputWidth, proxy.OutputHeight));

        var matched = new List<int>();
        var worst = double.PositiveInfinity;
        foreach (var time in times)
        {
            var expected = source.GetFrame(time);
            var actual = proxy.GetFrame(time);
            Assert.True(FrameIndex(expected) == FrameIndex(actual), $"{time} 秒：素材は {FrameIndex(expected)}、プロキシは {FrameIndex(actual)} フレーム目");
            Assert.Equal(expected.Time, actual.Time);
            var psnr = Psnr(expected.Bgra, actual.Bgra);
            worst = Math.Min(worst, psnr);
            Assert.True(psnr >= MinPsnr, $"{time} 秒の PSNR が {psnr:0.0}dB");
            matched.Add(FrameIndex(expected));
        }

        output.WriteLine($"最も低い PSNR: {worst:0.0}dB");
        return matched;
    }

    // フレームの色から、埋め込んだフレーム番号を読み取る（赤 = (N mod 8)×32+16、緑 = (N÷8)×32+16）
    private static int FrameIndex(DecodedFrame frame)
    {
        var (_, g, r, _) = frame.Bgra.GetPixel(frame.Bgra.Width / 2, frame.Bgra.Height / 2);
        return (int)Math.Round((r - 16) / 32.0) + 8 * (int)Math.Round((g - 16) / 32.0);
    }

    internal static double Psnr(BgraImage expected, BgraImage actual)
    {
        double sum = 0;
        long count = 0;
        for (var i = 0; i < expected.Pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                double d = expected.Pixels[i + c] - actual.Pixels[i + c];
                sum += d * d;
                count++;
            }
        }

        var mse = sum / count;
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
    }

    private static void AssertColor(string path, int red, int green, int blue)
    {
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));
        var image = decoder.GetFrame(Rational.Zero).Bgra;
        var (b, g, r, _) = image.GetPixel(image.Width / 2, image.Height / 2);
        Assert.True(Math.Abs(r - red) <= 6 && Math.Abs(g - green) <= 6 && Math.Abs(b - blue) <= 6, $"色が違います: R{r} G{g} B{b}（{red}, {green}, {blue} のはず）");
    }

    private sealed record ProbeResult(
        string Codec,
        (int Width, int Height) Size,
        VideoColorTags Color,
        int Packets,
        bool DecodeOrderIsDisplayOrder,
        int MaxKeyframeDistance);

    // 書いたファイルの映像のタグと、パケットの並び（B フレームの有無・キーフレームの間隔）を FFmpeg で直接調べる
    private static unsafe ProbeResult Probe(string path)
    {
        AVFormatContext* format = null;
        Assert.True(ffmpeg.avformat_open_input(&format, path, null, null) >= 0, $"開けません: {path}");
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            Assert.True(ffmpeg.avformat_find_stream_info(format, null) >= 0);
            var index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Assert.True(index >= 0);
            var parameters = format->streams[index]->codecpar;
            var codec = ffmpeg.avcodec_get_name(parameters->codec_id);

            var packets = 0;
            var inOrder = true;
            var lastPts = long.MinValue;
            var sinceKeyframe = 0;
            var maxDistance = 0;
            while (ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == index)
                {
                    packets++;
                    inOrder &= packet->pts == packet->dts && packet->pts > lastPts;
                    lastPts = packet->pts;
                    sinceKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 ? 1 : sinceKeyframe + 1;
                    maxDistance = Math.Max(maxDistance, sinceKeyframe);
                }

                ffmpeg.av_packet_unref(packet);
            }

            var color = new VideoColorTags(
                (int)parameters->color_primaries,
                (int)parameters->color_trc,
                (int)parameters->color_space,
                parameters->color_range == AVColorRange.AVCOL_RANGE_JPEG);
            return new ProbeResult(codec, (parameters->width, parameters->height), color, packets, inOrder, maxDistance);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }

    // 呼ばれたスレッドでそのまま知らせる IProgress（Progress<T> は SynchronizationContext に送るので、テストでは順番が崩れる）
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
