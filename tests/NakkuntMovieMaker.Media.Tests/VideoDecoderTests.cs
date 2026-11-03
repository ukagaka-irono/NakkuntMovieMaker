using NakkuntMovieMaker.Core.Time;
using Xunit.Sdk;

namespace NakkuntMovieMaker.Media.Tests;

public class VideoDecoderTests(ITestOutputHelper output)
{
    private static readonly Rational Ntsc = Rational.Fps29_97;

    public static TheoryData<HardwareDecoding> HardwareModes => [HardwareDecoding.Disabled, HardwareDecoding.Auto];

    // フレームの色から、埋め込んだフレーム番号を読み取る（赤 = (N mod 8)×32+16、緑 = (N÷8)×32+16）
    private static int FrameIndex(DecodedFrame frame)
    {
        var (_, g, r, _) = frame.Image.GetPixel(frame.Image.Width / 2, frame.Image.Height / 2);
        return (int)Math.Round((r - 16) / 32.0) + 8 * (int)Math.Round((g - 16) / 32.0);
    }

    // 長い動画（frames-long.mp4）は、青にも番号を埋め込む（青 = (N÷64)×32+16）
    private static int LongFrameIndex(DecodedFrame frame)
    {
        var (b, _, _, _) = frame.Image.GetPixel(frame.Image.Width / 2, frame.Image.Height / 2);
        return FrameIndex(frame) % 64 + 64 * (int)Math.Round((b - 16) / 32.0);
    }

    public static TheoryData<HardwareDecoding, ConversionQuality> AllModes => new()
    {
        { HardwareDecoding.Disabled, ConversionQuality.Accurate },
        { HardwareDecoding.Disabled, ConversionQuality.Fast },
        { HardwareDecoding.Auto, ConversionQuality.Accurate },
        { HardwareDecoding.Auto, ConversionQuality.Fast },
    };

    private VideoDecoder Open(string fixture, HardwareDecoding hardware, int? maxWidth = null, ConversionQuality quality = ConversionQuality.Accurate)
    {
        var decoder = VideoDecoder.Open(Ffmpeg.Fixture(fixture), new VideoDecoderOptions(maxWidth, null, hardware, Quality: quality));
        output.WriteLine($"ハードウェアデコード: {decoder.HardwareDevice ?? "なし"}");
        return decoder;
    }

    [Theory]
    [MemberData(nameof(HardwareModes))]
    public void ReturnsEveryFrameInOrder(HardwareDecoding hardware)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-2997.mp4", hardware);

        for (var k = 0; k < 60; k++)
        {
            var start = TimeMath.FrameToSeconds(k, Ntsc);
            var frame = decoder.GetFrame(start);
            Assert.Equal(k, FrameIndex(frame));
            Assert.Equal(start, frame.Time);
            // フレームの途中の時刻でも同じフレーム
            Assert.Equal(k, FrameIndex(decoder.GetFrame(start + new Rational(1, 60))));
        }
    }

    [Theory]
    [MemberData(nameof(HardwareModes))]
    public void ReturnsEveryFrameInReverse(HardwareDecoding hardware)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-2997.mp4", hardware);

        for (var k = 59; k >= 0; k--)
        {
            Assert.Equal(k, FrameIndex(decoder.GetFrame(TimeMath.FrameToSeconds(k, Ntsc))));
        }
    }

    [Theory]
    [MemberData(nameof(HardwareModes))]
    public void ReturnsTheRightFrameForRandomAccess(HardwareDecoding hardware)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-2997.mp4", hardware);
        var order = Enumerable.Range(0, 60).Concat(Enumerable.Range(0, 60)).OrderBy(_ => Random.Shared.Next()).ToList();
        var random = new Random(1234);
        order = [.. order.OrderBy(_ => random.Next())];

        foreach (var k in order)
        {
            Assert.Equal(k, FrameIndex(decoder.GetFrame(TimeMath.FrameToSeconds(k, Ntsc))));
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ReturnsTheRightFrameAfterLongJumps(HardwareDecoding hardware, ConversionQuality quality)
    {
        // 2 秒より先へ進むときはシークし、目的のフレームの手前までは参照されないフレームを飛ばしてデコードする
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-long.mp4", hardware, quality: quality);
        var random = new Random(42);

        foreach (var k in Enumerable.Range(0, 40).Select(_ => random.Next(0, 300)).Append(299).Append(0).Append(150))
        {
            Assert.Equal(k, LongFrameIndex(decoder.GetFrame(TimeMath.FrameToSeconds(k, 30))));
        }
    }

    [Theory]
    [MemberData(nameof(HardwareModes))]
    public void HandlesNearbyRequestsOutOfOrder(HardwareDecoding hardware)
    {
        // 複数のスレッドが近くのフレームを少し前後して求めても、正しいフレームを返す
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-long.mp4", hardware, quality: ConversionQuality.Fast);

        foreach (var k in new[] { 100, 103, 101, 102, 106, 104, 105, 109, 107, 108 })
        {
            Assert.Equal(k, LongFrameIndex(decoder.GetFrame(TimeMath.FrameToSeconds(k, 30))));
        }
    }

    [Fact]
    public void UsesTimestampsForVariableFrameRate()
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-vfr.mp4", HardwareDecoding.Disabled);

        // 0〜29 は 30fps、その後 0.5 秒あいて 30〜59 が 1.5 秒から始まる
        Assert.Equal(0, FrameIndex(decoder.GetFrame(Rational.Zero)));
        Assert.Equal(29, FrameIndex(decoder.GetFrame(new Rational(29, 30))));
        Assert.Equal(29, FrameIndex(decoder.GetFrame(new Rational(12, 10))));
        Assert.Equal(new Rational(16, 30), decoder.GetFrame(new Rational(29, 30)).Duration); // 29/30 秒 → 45/30 秒
        Assert.Equal(30, FrameIndex(decoder.GetFrame(new Rational(15, 10))));
        Assert.Equal(59, FrameIndex(decoder.GetFrame(new Rational(15, 10) + new Rational(29, 30))));
        Assert.Equal(10, FrameIndex(decoder.GetFrame(new Rational(10, 30))));
    }

    [Fact]
    public void ClampsTimesOutsideTheVideo()
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-2997.mp4", HardwareDecoding.Disabled);

        Assert.Equal(59, FrameIndex(decoder.GetFrame(new Rational(100, 1))));
        Assert.Null(decoder.GetFrame(new Rational(100, 1)).Duration);
        Assert.Equal(0, FrameIndex(decoder.GetFrame(new Rational(-5, 1))));
    }

    [Fact]
    public void ScalesToFitWithinTheMaximumSize()
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("frames-2997.mp4", HardwareDecoding.Disabled, maxWidth: 64);

        var frame = decoder.GetFrame(TimeMath.FrameToSeconds(10, Ntsc));

        Assert.Equal((128, 96), (decoder.SourceWidth, decoder.SourceHeight));
        Assert.Equal((64, 48), (frame.Image.Width, frame.Image.Height));
        Assert.Equal(10, FrameIndex(frame));
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ConvertsBt709ColorsCorrectly(HardwareDecoding hardware, ConversionQuality quality)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("color-c83c28-bt709.mp4", hardware, quality: quality);

        var (b, g, r, a) = decoder.GetFrame(Rational.Zero).Image.GetPixel(64, 48);

        // 元の色は RGB (200, 60, 40)。BT.601 で変換すると緑が 20 以上ずれる
        Assert.InRange(r, 194, 206);
        Assert.InRange(g, 54, 66);
        Assert.InRange(b, 34, 46);
        Assert.Equal(255, a);
    }

    [Fact]
    public void OpensImagesAsSingleFrameVideos()
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = Open("green-64x48.png", HardwareDecoding.Auto);

        var frame = decoder.GetFrame(new Rational(3, 1));

        Assert.Equal((64, 48), (frame.Image.Width, frame.Image.Height));
        // PNG の画素は (0, 127, 0)。RGB の素材は YUV を通さないので、そのままの値になる
        Assert.Equal((0, 127, 0, 255), frame.Image.GetPixel(10, 10) is var (b, g, r, a) ? (r, g, b, a) : default);
    }

    [Fact]
    public void RejectsFilesWithoutVideo()
    {
        Ffmpeg.RequireOrSkip();

        Assert.Throws<MediaException>(() => VideoDecoder.Open(Ffmpeg.Fixture("tone-1s.wav")));
        Assert.Throws<MediaException>(() => VideoDecoder.Open(Ffmpeg.Fixture("not-media.txt")));
        Assert.Throws<MediaException>(() => VideoDecoder.Open(Ffmpeg.Fixture("missing.mp4")));
    }
}
