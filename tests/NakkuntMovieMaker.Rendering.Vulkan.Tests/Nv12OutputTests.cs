using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// 書き出し用に、GPU で作業用の画像を NV12（BT.709・リミテッドレンジ、色差の位置は左）にする（G-34）。
/// 同じ場面を BGRA で描いた結果から、同じ式で CPU で NV12 にしたものと比べる
/// </summary>
public sealed class Nv12OutputTests : IDisposable
{
    private readonly VulkanDevice? _device;
    private readonly VulkanBackend? _gpu;

    public Nv12OutputTests()
    {
        try
        {
            _device = VulkanDevice.Create(Gpu.Options with { VideoDecode = false });
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

    [Theory]
    [InlineData("composition")]
    [InlineData("linear/transition-crossfade")]
    [InlineData("output/composition")]
    [InlineData("effect-blur")]
    public void ConvertsLikeTheReferenceFormula(string name)
    {
        var gpu = _gpu ?? throw SkipOrFail();
        Gpu.RequireRenderer();
        using var scene = RenderScenes.Create(name);

        var bgra = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);
        using var result = gpu.Render(scene.Plan, scene.Media, new RenderRequest(scene.Scale, YuvOutput.Bt709), TestContext.Current.CancellationToken);
        var planes = result.ReadPlanes();

        Assert.NotNull(planes);
        Assert.Equal((bgra.Width, bgra.Height, VideoPlaneFormat.Nv12), (planes.Width, planes.Height, planes.Format));
        Assert.Equal((VideoMatrix.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left), (planes.Color.Matrix, planes.Color.Range, planes.Color.ChromaLocation));

        // BGRA は 8 ビットに丸めてあるので、1 か 2 の差は出る
        var (lumaError, chromaError) = Compare(Reference(bgra), planes);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: 輝度の最大の差 {lumaError}、色差の最大の差 {chromaError}");
        Assert.InRange(lumaError, 0, 2);
        Assert.InRange(chromaError, 0, 2);

        // BGRA として読むと、NV12 を CPU で RGB にしたもの
        Assert.True(ImageCompare.Compare(bgra, result.ReadBgra()).Psnr >= 30);
    }

    [Fact]
    public void OddSizesFallBackToBgra()
    {
        // NV12 は縦横が偶数のときだけ作る（奇数なら、BGRA で読む）
        var gpu = _gpu ?? throw SkipOrFail();
        Gpu.RequireRenderer();
        using var scene = RenderScenes.Create("composition");

        using var result = gpu.Render(scene.Plan with { Width = 321 }, scene.Media, new RenderRequest(1, YuvOutput.Bt709), TestContext.Current.CancellationToken);

        Assert.Null(result.ReadPlanes());
        Assert.Equal(321, result.ReadBgra().Width);
    }

    private static Exception SkipOrFail()
    {
        using var device = Gpu.CreateOrSkip();
        return new InvalidOperationException("GPU のデバイスは作れたが、描画の準備ができませんでした");
    }

    // BGRA（premultiplied をそのまま、黒の上に重ねたものとして）を、to_nv12.slang と同じ式で NV12 にする
    private static (byte[] Luma, byte[] Chroma) Reference(BgraImage image)
    {
        const double Kr = 0.2126, Kb = 0.0722;
        var width = image.Width;
        var height = image.Height;
        (double R, double G, double B) At(int x, int y)
        {
            x = Math.Clamp(x, 0, width - 1);
            y = Math.Clamp(y, 0, height - 1);
            var i = y * image.Stride + x * 4;
            return (image.Pixels[i + 2] / 255.0, image.Pixels[i + 1] / 255.0, image.Pixels[i] / 255.0);
        }

        static double Luma((double R, double G, double B) c) => Kr * c.R + (1 - Kr - Kb) * c.G + Kb * c.B;
        static byte Unorm(double value) => (byte)Math.Clamp(Math.Floor(value + 0.5), 0, 255);

        var luma = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                luma[y * width + x] = Unorm(16 + 219 * Luma(At(x, y)));
            }
        }

        var chromaWidth = width / 2;
        var chroma = new byte[chromaWidth * 2 * (height / 2)];
        for (var j = 0; j < height / 2; j++)
        {
            for (var i = 0; i < chromaWidth; i++)
            {
                double cb = 0, cr = 0;
                for (var row = 0; row < 2; row++)
                {
                    for (var tap = -1; tap <= 1; tap++)
                    {
                        var weight = tap == 0 ? 0.5 : 0.25;
                        var c = At(2 * i + tap, 2 * j + row);
                        var y = Luma(c);
                        cb += weight * (c.B - y) / (2 * (1 - Kb));
                        cr += weight * (c.R - y) / (2 * (1 - Kr));
                    }
                }

                chroma[(j * chromaWidth + i) * 2] = Unorm(128 + 224 * cb / 2);
                chroma[(j * chromaWidth + i) * 2 + 1] = Unorm(128 + 224 * cr / 2);
            }
        }

        return (luma, chroma);
    }

    private static (int Luma, int Chroma) Compare((byte[] Luma, byte[] Chroma) expected, VideoPlanes actual)
    {
        int lumaError = 0, chromaError = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                lumaError = Math.Max(lumaError, Math.Abs(expected.Luma[y * actual.Width + x] - actual.Luma[y * actual.LumaStride + x]));
            }
        }

        var rowBytes = actual.ChromaWidth * 2;
        for (var y = 0; y < actual.ChromaHeight; y++)
        {
            for (var x = 0; x < rowBytes; x++)
            {
                chromaError = Math.Max(chromaError, Math.Abs(expected.Chroma[y * rowBytes + x] - actual.Chroma[y * actual.ChromaStride + x]));
            }
        }

        return (lumaError, chromaError);
    }
}
