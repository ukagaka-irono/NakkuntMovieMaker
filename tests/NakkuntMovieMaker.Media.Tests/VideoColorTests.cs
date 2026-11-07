using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

public class VideoColorTests(ITestOutputHelper output)
{
    // テスト用の動画はどれも、R'G'B' (200, 60, 40) の単色を、それぞれの行列・範囲で YUV にしたもの（Fixtures/README.md）
    private const int ExpectedR = 200;
    private const int ExpectedG = 60;
    private const int ExpectedB = 40;

    public static TheoryData<string, VideoColorInfo> TaggedFixtures => new()
    {
        {
            "color-c83c28-bt601-720p.mp4",
            new VideoColorInfo(
                VideoColorPrimaries.Smpte170M, VideoTransferCharacteristic.Smpte170M, VideoMatrix.Smpte170M,
                VideoColorRange.Limited, VideoChromaLocation.Left)
        },
        {
            "color-c83c28-bt709-full.mp4",
            new VideoColorInfo(
                VideoColorPrimaries.Bt709, VideoTransferCharacteristic.Bt709, VideoMatrix.Bt709,
                VideoColorRange.Full, VideoChromaLocation.Left)
        },
        {
            "color-c83c28-bt2020-pq.mp4",
            new VideoColorInfo(
                VideoColorPrimaries.Bt2020, VideoTransferCharacteristic.Pq, VideoMatrix.Bt2020Ncl,
                VideoColorRange.Limited, VideoChromaLocation.Left, PqMastering, PqLightLevel)
        },
        {
            "color-c83c28-bt2020-hlg.mp4",
            new VideoColorInfo(
                VideoColorPrimaries.Bt2020, VideoTransferCharacteristic.Hlg, VideoMatrix.Bt2020Ncl,
                VideoColorRange.Limited, VideoChromaLocation.Left)
        },
    };

    public static TheoryData<string, HardwareDecoding, ConversionQuality> ColorFixturesAndModes
    {
        get
        {
            var data = new TheoryData<string, HardwareDecoding, ConversionQuality>();
            foreach (var file in new[]
                     {
                         "color-c83c28-bt601-720p.mp4", "color-c83c28-bt709-full.mp4", "color-c83c28-bt709.mp4",
                         "color-c83c28-bt2020-pq.mp4", "color-c83c28-bt2020-hlg.mp4",
                     })
            {
                foreach (var hardware in new[] { HardwareDecoding.Disabled, HardwareDecoding.Auto })
                {
                    foreach (var quality in new[] { ConversionQuality.Accurate, ConversionQuality.Fast })
                    {
                        data.Add(file, hardware, quality);
                    }
                }
            }

            return data;
        }
    }

    // PQ の動画に付けたマスタリングの情報（x265 の master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)）
    private static readonly MasteringDisplayInfo PqMastering = new(
        new Chromaticity(0.68, 0.32),
        new Chromaticity(0.265, 0.69),
        new Chromaticity(0.15, 0.06),
        new Chromaticity(0.3127, 0.329),
        0.005,
        1000);

    private static readonly ContentLightLevelInfo PqLightLevel = new(1000, 400);

    [Theory]
    [MemberData(nameof(TaggedFixtures))]
    public void ProbeReadsColorTags(string file, VideoColorInfo expected)
    {
        Ffmpeg.RequireOrSkip();

        var color = MediaProbe.Probe(Ffmpeg.Fixture(file)).Video!.Color;
        output.WriteLine(color.ToString());

        AssertColor(expected, color);
        Assert.Equal(VideoColorGuesses.None, color.Guessed);
    }

    [Theory]
    [MemberData(nameof(TaggedFixtures))]
    public void DecodedFramesCarryColorTags(string file, VideoColorInfo expected)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture(file));

        var frame = decoder.GetFrame(Rational.Zero);
        output.WriteLine(frame.Color.ToString());

        AssertColor(expected, frame.Color);
        Assert.Equal(VideoColorGuesses.None, frame.Color.Guessed);
    }

    [Theory]
    [MemberData(nameof(ColorFixturesAndModes))]
    public void DecodesWithTheTaggedMatrixAndRange(string file, HardwareDecoding hardware, ConversionQuality quality)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture(file), new VideoDecoderOptions(Hardware: hardware, Quality: quality));
        output.WriteLine($"ハードウェアデコード: {decoder.HardwareDevice ?? "なし"}");

        var frame = decoder.GetFrame(Rational.Zero);
        var (b, g, r, a) = frame.Bgra.GetPixel(frame.Bgra.Width / 2, frame.Bgra.Height / 2);
        output.WriteLine($"{file}: R={r} G={g} B={b}");

        // 行列を取り違えると緑が 8 以上（BT.2020 を BT.709 とみなした場合。BT.601 ↔ BT.709 なら 20 以上）、範囲を取り違えると 10 以上ずれる
        Assert.InRange(r, ExpectedR - 5, ExpectedR + 5);
        Assert.InRange(g, ExpectedG - 5, ExpectedG + 5);
        Assert.InRange(b, ExpectedB - 5, ExpectedB + 5);
        Assert.Equal(255, a);
    }

    [Fact]
    public void TagsOverrideTheSizeBasedGuess()
    {
        Ffmpeg.RequireOrSkip();

        // 720p なので推測なら BT.709 だが、タグは BT.601
        var bt601 = MediaProbe.Probe(Ffmpeg.Fixture("color-c83c28-bt601-720p.mp4")).Video!;
        Assert.Equal(720, bt601.Height);
        Assert.Equal(VideoMatrix.Smpte170M, bt601.Color.Matrix);

        // 96 本なので推測なら BT.601 だが、タグは BT.709
        var bt709 = MediaProbe.Probe(Ffmpeg.Fixture("color-c83c28-bt709-full.mp4")).Video!;
        Assert.Equal(96, bt709.Height);
        Assert.Equal(VideoMatrix.Bt709, bt709.Color.Matrix);
    }

    [Fact]
    public void GuessesBt601ForUntaggedSdVideo()
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture("frames-2997.mp4"));

        foreach (var color in new[] { MediaProbe.Probe(Ffmpeg.Fixture("frames-2997.mp4")).Video!.Color, decoder.GetFrame(Rational.Zero).Color })
        {
            Assert.Equal(VideoMatrix.Smpte170M, color.Matrix);
            Assert.Equal(VideoColorPrimaries.Smpte170M, color.Primaries);
            Assert.Equal(VideoTransferCharacteristic.Bt709, color.Transfer);
            Assert.Equal(VideoColorRange.Limited, color.Range);
            Assert.Equal(
                VideoColorGuesses.Primaries | VideoColorGuesses.Transfer | VideoColorGuesses.Matrix | VideoColorGuesses.Range,
                color.Guessed);
            Assert.Equal(InputColorSpace.SrgbRec709, color.SuggestedInputColorSpace);
        }
    }

    [Theory]
    [InlineData("green-64x48.png")]
    [InlineData("testsrc-1s.webm")]
    public void RgbSourcesAreFullRangeRgb(string file)
    {
        Ffmpeg.RequireOrSkip();
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture(file));

        foreach (var color in new[] { MediaProbe.Probe(Ffmpeg.Fixture(file)).Video!.Color, decoder.GetFrame(Rational.Zero).Color })
        {
            Assert.Equal(VideoMatrix.Rgb, color.Matrix);
            Assert.Equal(VideoColorRange.Full, color.Range);
            Assert.Equal(VideoTransferCharacteristic.Srgb, color.Transfer);
            Assert.Equal(VideoColorPrimaries.Bt709, color.Primaries);
            Assert.Equal(InputColorSpace.SrgbRec709, color.SuggestedInputColorSpace);
        }
    }

    [Fact]
    public void JpegIsFullRangeBt601()
    {
        Ffmpeg.RequireOrSkip();

        var color = MediaProbe.Probe(Ffmpeg.Fixture("yellow-64x48.jpg")).Video!.Color;

        Assert.Equal(VideoMatrix.Bt470BG, color.Matrix);
        Assert.Equal(VideoColorRange.Full, color.Range);
        Assert.Equal(VideoChromaLocation.Center, color.ChromaLocation);
    }

    [Theory]
    [InlineData("color-c83c28-bt601-720p.mp4", InputColorSpace.SrgbRec709, "sRGB/Rec.709")]
    [InlineData("color-c83c28-bt709-full.mp4", InputColorSpace.SrgbRec709, "sRGB/Rec.709")]
    [InlineData("color-c83c28-bt2020-pq.mp4", InputColorSpace.Rec2100Pq, "Rec.2100-PQ")]
    [InlineData("color-c83c28-bt2020-hlg.mp4", InputColorSpace.Rec2100Hlg, "Rec.2100-HLG")]
    public void SuggestsAnInputColorSpace(string file, InputColorSpace expected, string name)
    {
        Ffmpeg.RequireOrSkip();

        var color = MediaProbe.Probe(Ffmpeg.Fixture(file)).Video!.Color;

        Assert.Equal(expected, color.SuggestedInputColorSpace);
        Assert.Equal(name, color.SuggestedInputColorSpace.Name());
        Assert.Equal(expected is InputColorSpace.Rec2100Pq or InputColorSpace.Rec2100Hlg, color.IsHdr);
    }

    [Theory]
    [InlineData(VideoColorPrimaries.Bt2020, VideoTransferCharacteristic.Bt2020_10, InputColorSpace.Rec2020)]
    [InlineData(VideoColorPrimaries.DisplayP3, VideoTransferCharacteristic.Srgb, InputColorSpace.DisplayP3)]
    [InlineData(VideoColorPrimaries.DciP3, VideoTransferCharacteristic.Smpte428, InputColorSpace.DisplayP3)]
    [InlineData(VideoColorPrimaries.Bt470BG, VideoTransferCharacteristic.Gamma28, InputColorSpace.SrgbRec709)]
    [InlineData(VideoColorPrimaries.Bt709, VideoTransferCharacteristic.Pq, InputColorSpace.Rec2100Pq)]
    public void SuggestionFollowsTransferThenPrimaries(
        VideoColorPrimaries primaries, VideoTransferCharacteristic transfer, InputColorSpace expected)
    {
        var color = new VideoColorInfo(primaries, transfer, VideoMatrix.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left);

        Assert.Equal(expected, color.SuggestedInputColorSpace);
    }

    [Theory]
    [InlineData(1080, VideoMatrix.Bt709, VideoColorPrimaries.Bt709)]
    [InlineData(720, VideoMatrix.Bt709, VideoColorPrimaries.Bt709)]
    [InlineData(576, VideoMatrix.Smpte170M, VideoColorPrimaries.Bt470BG)]
    [InlineData(480, VideoMatrix.Smpte170M, VideoColorPrimaries.Smpte170M)]
    public void ResolveGuessesBySize(int height, VideoMatrix matrix, VideoColorPrimaries primaries)
    {
        var color = VideoColorInfo.Resolve(null, null, null, null, VideoChromaLocation.Unspecified, height, isRgb: false);

        Assert.Equal(matrix, color.Matrix);
        Assert.Equal(primaries, color.Primaries);
        Assert.Equal(VideoColorRange.Limited, color.Range);
    }

    [Fact]
    public void ResolveGuessesPrimariesFromABt2020Matrix()
    {
        var color = VideoColorInfo.Resolve(null, null, VideoMatrix.Bt2020Ncl, VideoColorRange.Limited, VideoChromaLocation.TopLeft, 2160, isRgb: false);

        Assert.Equal(VideoColorPrimaries.Bt2020, color.Primaries);
        Assert.Equal(VideoColorGuesses.Primaries | VideoColorGuesses.Transfer, color.Guessed);
    }

    private static void AssertColor(VideoColorInfo expected, VideoColorInfo actual)
    {
        Assert.Equal(expected.Primaries, actual.Primaries);
        Assert.Equal(expected.Transfer, actual.Transfer);
        Assert.Equal(expected.Matrix, actual.Matrix);
        Assert.Equal(expected.Range, actual.Range);
        Assert.Equal(expected.ChromaLocation, actual.ChromaLocation);
        Assert.Equal(expected.ContentLightLevel, actual.ContentLightLevel);
        if (expected.MasteringDisplay is not { } mastering)
        {
            Assert.Null(actual.MasteringDisplay);
            return;
        }

        var actualMastering = Assert.IsType<MasteringDisplayInfo>(actual.MasteringDisplay);
        AssertPoint(mastering.Red, actualMastering.Red);
        AssertPoint(mastering.Green, actualMastering.Green);
        AssertPoint(mastering.Blue, actualMastering.Blue);
        AssertPoint(mastering.WhitePoint, actualMastering.WhitePoint);
        Assert.Equal(mastering.MinLuminance!.Value, actualMastering.MinLuminance!.Value, 1e-6);
        Assert.Equal(mastering.MaxLuminance!.Value, actualMastering.MaxLuminance!.Value, 1e-6);
    }

    private static void AssertPoint(Chromaticity? expected, Chromaticity? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected!.Value.X, actual.Value.X, 1e-6);
        Assert.Equal(expected.Value.Y, actual.Value.Y, 1e-6);
    }
}
