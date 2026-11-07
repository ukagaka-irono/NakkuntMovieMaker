using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

/// <summary>デコードした映像を、YUV の平面のまま渡す（GPU で RGB にするため）</summary>
public sealed class VideoPlanesTests(ITestOutputHelper output)
{
    public static TheoryData<string, HardwareDecoding> Fixtures
    {
        get
        {
            var data = new TheoryData<string, HardwareDecoding>();
            foreach (var file in new[]
                     {
                         "frames-long.mp4", "frames-2997.mp4", "color-c83c28-bt601-720p.mp4", "color-c83c28-bt709-full.mp4",
                         "color-c83c28-bt2020-pq.mp4", "color-c83c28-bt2020-hlg.mp4",
                     })
            {
                data.Add(file, HardwareDecoding.Disabled);
                data.Add(file, HardwareDecoding.Auto);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void PlanesConvertLikeSwscale(string file, HardwareDecoding hardware)
    {
        // 平面から CPU で RGB にしたもの（GPU のシェーダーと同じ式）が、swscale（Accurate）の結果と 42dB 以上で一致する
        Ffmpeg.RequireOrSkip();
        using var planesDecoder = VideoDecoder.Open(Ffmpeg.Fixture(file), new VideoDecoderOptions(Hardware: hardware, Output: VideoFrameOutput.Planes));
        using var bgraDecoder = VideoDecoder.Open(Ffmpeg.Fixture(file), new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));

        foreach (var time in new[] { Rational.Zero, new Rational(1, 2) })
        {
            var planes = planesDecoder.GetFrame(time);
            var reference = bgraDecoder.GetFrame(time);

            Assert.Null(planes.Image);
            Assert.NotNull(planes.Planes);
            Assert.Equal(file.Contains("bt2020", StringComparison.Ordinal) ? VideoPlaneFormat.P010 : VideoPlaneFormat.Nv12, planes.Planes.Format);
            Assert.Equal((reference.Bgra.Width, reference.Bgra.Height), (planes.Planes.Width, planes.Planes.Height));
            Assert.Equal(reference.Time, planes.Time);

            var psnr = ProxyTests.Psnr(reference.Bgra, planes.Bgra);
            output.WriteLine($"{file}（{hardware}、{planesDecoder.HardwareDevice ?? "ソフトウェア"}）{time} 秒：PSNR {psnr:F1}dB");
            Assert.True(psnr >= 42, $"PSNR {psnr:F1}dB");
        }
    }

    [Fact]
    public void OtherFormatsFallBackToBgra()
    {
        // 4:2:0 でない映像（テストパターンの VP9 は 4:4:4）は、平面のまま渡す設定でも BGRA の画像にする
        Ffmpeg.RequireOrSkip();
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture("testsrc-1s.webm"), new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled, Output: VideoFrameOutput.Planes));

        var frame = decoder.GetFrame(Rational.Zero);

        Assert.NotNull(frame.Image);
        Assert.Null(frame.Planes);
    }

    [Fact]
    public void BgraOutputIsUnchanged()
    {
        // 既定（BGRA）では、これまでどおり swscale で RGB にした画像を返し、平面は返さない
        Ffmpeg.RequireOrSkip();
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture("frames-long.mp4"), new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));

        var frame = decoder.GetFrame(Rational.Zero);

        Assert.NotNull(frame.Image);
        Assert.Null(frame.Planes);
        Assert.Same(frame.Image, frame.Bgra);
    }

    [Fact]
    public void ConversionUsesTheMatrixRangeAndChromaLocation()
    {
        // 1 画素の灰色と色：BT.709 のリミテッドで (Y, Cb, Cr) = (116, 92, 174) は、だいたい R'G'B' (200, 100, 40)
        var luma = new byte[] { 116, 116, 116, 116 };
        var chroma = new byte[] { 92, 174 };
        var color = new VideoColorInfo(VideoColorPrimaries.Bt709, VideoTransferCharacteristic.Bt709, VideoMatrix.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left);
        var image = YuvConversion.ToBgra(new VideoPlanes(VideoPlaneFormat.Nv12, 2, 2, luma, 2, chroma, 2, color));

        var (b, g, r, a) = image.GetPixel(0, 0);
        Assert.InRange(r, 198, 202);
        Assert.InRange(g, 98, 103);
        Assert.InRange(b, 38, 43);
        Assert.Equal(255, a);
    }
}
