using System.Numerics;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// GPU 版を使う前の確かめ。小さな場面（変形・不透明度・合成モード・切り抜きを付けた画像）を GPU 版と CPU 版で描いて比べる。
/// ドライバーの不具合などで結果が大きく違えば、GPU 版を使わない。
/// 図形（Skia の GPU 版で描く）も比べ、違いが大きければ、図形と文字は CPU で描いて送るようにする（GPU 版は使う）
/// </summary>
public static class GpuSelfTest
{
    // GPU 版と CPU 版の違いの許容（PSNR、dB）。縁のアンチエイリアスの違いを見込む
    private const double MinPsnr = 40;

    // Skia の GPU 版で描いた図形の許容（縁のアンチエイリアスが CPU 版と少し違う。ParityTests の文字と図形の基準と同じ）
    private const double MinVectorPsnr = 38;

    /// <summary>確かめる。問題がなければ null、あれば理由（利用者に表示できる文）</summary>
    public static string? Run(VulkanBackend gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var media = new SelfTestMedia();
        var plan = new FramePlan(96, 64, new RgbaColor(30, 40, 50),
        [
            Image(media.Gradient, Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateTranslation(48, 32), 1, BlendMode.Normal, Crop.None),
            Image(media.Checker, Matrix3x2.CreateScale(0.8f) * Matrix3x2.CreateTranslation(60, 28), 0.7, BlendMode.Screen, new Crop(2, 3, 1, 0)),
            Image(media.Checker, Matrix3x2.CreateTranslation(30, 40), 1, BlendMode.Multiply, Crop.None),
        ]);

        try
        {
            using var cpu = new SkiaCpuBackend();
            var expected = cpu.Render(plan, media);
            var actual = gpu.Render(plan, media);
            var psnr = Psnr(expected, actual);
            if (psnr < MinPsnr)
            {
                return $"GPU（{gpu.Device.Info.Name}）で描いた結果が正しくありません（PSNR {psnr:F1}dB）";
            }

            if (gpu.UsesSkiaGpu)
            {
                var shapes = new FramePlan(96, 64, new RgbaColor(30, 40, 50),
                [
                    new DrawNode(null, new ShapeContent(new ShapeStyle(ShapeKind.Ellipse, 50, 30, new SolidPaint(new RgbaColor(240, 180, 40)), RgbaColor.White, 3)),
                        Crop.None, Matrix3x2.CreateRotation(0.4f) * Matrix3x2.CreateTranslation(48, 32), 0.8, BlendMode.Normal),
                ]);
                var vector = Psnr(cpu.Render(shapes, media), gpu.Render(shapes, media));
                if (vector < MinVectorPsnr)
                {
                    gpu.DisableSkiaGpu($"Skia の GPU 版で描いた図形が CPU 版と違うので、文字と図形は CPU で描きます（PSNR {vector:F1}dB）");
                }
            }

            return null;
        }
        catch (VulkanException ex)
        {
            return ex.Message;
        }
    }

    private static DrawNode Image(AssetId asset, Matrix3x2 transform, double opacity, BlendMode blend, Crop crop) =>
        new(null, new ImageContent(asset), crop, transform, opacity, blend);

    private static double Psnr(BgraImage expected, BgraImage actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return 0;
        }

        var squared = 0.0;
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            var difference = expected.Pixels[i] - actual.Pixels[i];
            squared += difference * difference;
        }

        return squared == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squared / expected.Pixels.Length));
    }

    // 確かめるための素材（グラデーションと市松模様。半透明の画素も含める）
    private sealed class SelfTestMedia : IMediaSource
    {
        private readonly Dictionary<AssetId, SourceImage> _images = [];

        public SelfTestMedia()
        {
            Gradient = Add(Make(40, 24, (x, y) => ((byte)(x * 6), (byte)(y * 10), 128, 255)));
            Checker = Add(Make(16, 16, (x, y) => ((x / 4 + y / 4) % 2 == 0 ? ((byte)255, (byte)200, (byte)0, (byte)255) : ((byte)0, (byte)60, (byte)200, (byte)128))));
        }

        public AssetId Gradient { get; }

        public AssetId Checker { get; }

        public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale) => null;

        public SourceImage? GetImage(AssetId asset, double scale) => _images.GetValueOrDefault(asset);

        private AssetId Add(BgraImage image)
        {
            var id = AssetId.New();
            _images[id] = new SourceImage(image);
            return id;
        }

        // straight alpha の色から、premultiplied の BGRA を作る
        private static BgraImage Make(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> color)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b, a) = color(x, y);
                    var i = (y * width + x) * 4;
                    pixels[i] = (byte)((b * a + 127) / 255);
                    pixels[i + 1] = (byte)((g * a + 127) / 255);
                    pixels[i + 2] = (byte)((r * a + 127) / 255);
                    pixels[i + 3] = a;
                }
            }

            return new BgraImage(width, height, pixels);
        }
    }
}
