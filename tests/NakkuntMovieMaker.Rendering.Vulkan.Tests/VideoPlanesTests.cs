using System.Diagnostics;
using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Color;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// 動画のフレームを YUV の平面のまま受け取り、GPU で RGB にする（F-29）。CPU 版は同じ平面を YuvConversion で RGB にしたものを描くので、
/// 両方の結果が一致すれば、シェーダーが CPU と同じ式で変換している
/// </summary>
public sealed class VideoPlanesTests : IDisposable
{
    private readonly VulkanDevice? _device;
    private readonly VulkanBackend? _gpu;
    private readonly SkiaCpuBackend _cpu = new();

    public VideoPlanesTests()
    {
        try
        {
            _device = VulkanDevice.Create(Gpu.Options);
            _gpu = new VulkanBackend(_device);
        }
        catch (VulkanException)
        {
        }
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _device?.Dispose();
    }

    public static TheoryData<VideoPlaneFormat, VideoMatrix, VideoColorRange, VideoChromaLocation, bool> Cases => new()
    {
        { VideoPlaneFormat.Nv12, VideoMatrix.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left, false },
        { VideoPlaneFormat.Nv12, VideoMatrix.Smpte170M, VideoColorRange.Full, VideoChromaLocation.Center, false },
        { VideoPlaneFormat.P010, VideoMatrix.Bt2020Ncl, VideoColorRange.Limited, VideoChromaLocation.TopLeft, false },
        { VideoPlaneFormat.P010, VideoMatrix.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConvertsPlanesLikeTheCpu(VideoPlaneFormat format, VideoMatrix matrix, VideoColorRange range, VideoChromaLocation location, bool withInput)
    {
        var gpu = Require();
        var color = new VideoColorInfo(VideoColorPrimaries.Bt709, VideoTransferCharacteristic.Bt709, matrix, range, location);
        var planes = Pattern(format, 97, 61, color);
        var media = new PlanesMedia(planes, withInput ? TestLuts.Grade : null);
        var plan = Plan(160, 100, Matrix3x2.CreateRotation(0.2f) * Matrix3x2.CreateTranslation(80, 50));

        var effectNodes = gpu.CpuLayers;
        var expected = _cpu.Render(plan, media, 1, TestContext.Current.CancellationToken);
        var actual = gpu.Render(plan, media, 1, TestContext.Current.CancellationToken);

        Assert.True(media.PlanesRequested, "GPU 版が平面を受け取っていません");
        Assert.Equal(effectNodes, gpu.CpuLayers); // CPU で描いて送っていない
        var difference = ImageCompare.Compare(expected, actual);
        TestContext.Current.TestOutputHelper?.WriteLine($"{format} {matrix} {range} {location} {(withInput ? "入力の変換あり" : "")}: {difference}");
        Assert.True(difference.Psnr >= 42, $"CPU 版と違います（{difference}）");
    }

    [Fact]
    public void MeasuresFullHdUploadAndConversion()
    {
        // 1080p の NV12 を送って RGB にし、出力に重ねて読み戻すまでの時間（記録用。速さの基準は確かめない）
        var gpu = Require();
        var color = new VideoColorInfo(VideoColorPrimaries.Bt709, VideoTransferCharacteristic.Bt709, VideoMatrix.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left);
        var media = new PlanesMedia(Pattern(VideoPlaneFormat.Nv12, 1920, 1080, color), null);
        var plan = Plan(1920, 1080, Matrix3x2.CreateTranslation(960, 540));
        gpu.Render(plan, media, 1, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        const int Frames = 10;
        for (var i = 0; i < Frames; i++)
        {
            gpu.Render(plan, media, 1, TestContext.Current.CancellationToken);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"1080p の NV12：1 枚 {watch.Elapsed.TotalMilliseconds / Frames:F1}ms（送る・RGB にする・重ねる・読み戻す）");
    }

    private VulkanBackend Require()
    {
        if (_gpu is null)
        {
            using var device = Gpu.CreateOrSkip();
            throw new InvalidOperationException("GPU のデバイスは作れたが、描画の準備ができませんでした");
        }

        Gpu.RequireRenderer();
        return _gpu;
    }

    private static readonly AssetId Video = AssetId.New();

    private static FramePlan Plan(int width, int height, Matrix3x2 transform) =>
        new(width, height, new RgbaColor(20, 30, 40), new EquatableArray<RenderNode>(
            [new DrawNode(null, new VideoFrameContent(Video, Rational.Zero), Crop.None with { Left = 3, Bottom = 2 }, transform, 1, BlendMode.Normal)]));

    // 色が場所ごとになめらかに変わり、細かい模様もある YUV の平面
    private static VideoPlanes Pattern(VideoPlaneFormat format, int width, int height, VideoColorInfo color)
    {
        var bytes = format == VideoPlaneFormat.P010 ? 2 : 1;
        var bits = format == VideoPlaneFormat.P010 ? 10 : 8;
        var max = (1 << bits) - 1;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var luma = new byte[width * height * bytes];
        var chroma = new byte[chromaWidth * 2 * chromaHeight * bytes];
        void Put(byte[] plane, int index, double value)
        {
            var sample = (int)Math.Clamp(Math.Round(value * max), 0, max);
            if (bytes == 2)
            {
                var shifted = sample << 6;
                plane[index * 2] = (byte)shifted;
                plane[index * 2 + 1] = (byte)(shifted >> 8);
            }
            else
            {
                plane[index] = (byte)sample;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Put(luma, y * width + x, 0.1 + 0.8 * x / width + ((x / 4 + y / 4) % 2 == 0 ? 0.05 : 0));
            }
        }

        for (var y = 0; y < chromaHeight; y++)
        {
            for (var x = 0; x < chromaWidth; x++)
            {
                Put(chroma, (y * chromaWidth + x) * 2, 0.5 + 0.35 * Math.Sin(x * 0.3));
                Put(chroma, (y * chromaWidth + x) * 2 + 1, 0.5 + 0.35 * Math.Cos(y * 0.25));
            }
        }

        return new VideoPlanes(format, width, height, luma, width * bytes, chroma, chromaWidth * 2 * bytes, color);
    }

    // 1 つの動画（平面）を返す素材。CPU 版には、同じ平面を YuvConversion で RGB にしたもの（と入力の変換）を返す
    private sealed class PlanesMedia(VideoPlanes planes, ColorLut? input) : IMediaSource
    {
        private readonly BgraImage _bgra = input is null ? YuvConversion.ToBgra(planes) : ColorLutImage.Apply(input, YuvConversion.ToBgra(planes));

        public bool PlanesRequested { get; private set; }

        public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale) => new(_bgra);

        public SourceImage? GetImage(AssetId asset, double scale) => null;

        public VideoPlaneFrame? GetVideoPlanes(AssetId asset, Rational time)
        {
            PlanesRequested = true;
            return new VideoPlaneFrame(planes, planes.Width, planes.Height, input);
        }
    }
}
