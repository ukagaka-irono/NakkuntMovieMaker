using System.Numerics;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia;

/// <summary>
/// Skia の CPU 描画で FramePlan を実行する。状態を持たないので、複数のスレッドから同時に
/// 別々のフレームを描いてよい（IMediaSource がスレッドセーフであれば）
/// </summary>
public sealed unsafe class SkiaCpuBackend : IRenderBackend
{
    // Skia にない「減算」：重ねる色を下の色から引く（色は premultiplied なので、不透明度のぶんだけ引かれる）
    private const string SubtractSksl = """
        half4 main(half4 src, half4 dst) {
            return half4(max(dst.rgb - src.rgb, 0.0), src.a + dst.a * (1.0 - src.a));
        }
        """;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static readonly Lazy<SKBlender> SubtractBlender = new(() =>
    {
        using var effect = SKRuntimeEffect.CreateBlender(SubtractSksl, out var errors)
            ?? throw new InvalidOperationException($"減算の合成を準備できませんでした: {errors}");
        return effect.ToBlender();
    });

    public BgraImage Render(FramePlan plan, IMediaSource media, double scale = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        if (!(scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "倍率は 0 より大きくしてください");
        }

        var width = Math.Max(1, (int)Math.Round(plan.Width * scale));
        var height = Math.Max(1, (int)Math.Round(plan.Height * scale));
        var pixels = new byte[width * height * 4];
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        fixed (byte* output = pixels)
        {
            using var surface = SKSurface.Create(info, (nint)output, width * 4)
                ?? throw new InvalidOperationException("描画先を準備できませんでした");
            var canvas = surface.Canvas;
            var background = plan.Background;
            canvas.Clear(new SKColor(background.R, background.G, background.B, background.A));

            // シーンの座標 → 出力の座標
            var outputScale = SKMatrix.CreateScale((float)(width / (double)plan.Width), (float)(height / (double)plan.Height));
            foreach (var node in plan.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is DrawNode draw)
                {
                    Draw(canvas, draw, media, outputScale);
                }
            }

            canvas.Flush();
        }

        return new BgraImage(width, height, pixels);
    }

    private static void Draw(SKCanvas canvas, DrawNode node, IMediaSource media, SKMatrix outputScale)
    {
        var matrix = SKMatrix.Concat(outputScale, ToSkia(node.Transform));
        var hint = Math.Max(
            Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY),
            Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY));

        var source = node.Content switch
        {
            VideoFrameContent video => media.GetVideoFrame(video.Asset, video.SourceTime, hint),
            ImageContent image => media.GetImage(image.Asset, hint),

            // 文字と図形は、テキストと図形の手順で描けるようにする
            _ => null,
        };
        if (source is null)
        {
            return;
        }

        DrawImage(canvas, node, source, matrix);
    }

    // 素材を切り抜いて、残った部分の中心をローカル座標の原点に置いて描く
    private static void DrawImage(SKCanvas canvas, DrawNode node, SourceImage source, SKMatrix matrix)
    {
        var crop = node.Crop;
        var visibleWidth = source.Width - crop.Left - crop.Right;
        var visibleHeight = source.Height - crop.Top - crop.Bottom;
        if (visibleWidth <= 0 || visibleHeight <= 0)
        {
            return;
        }

        // 切り抜く範囲を、素材の画像（縮小されていることがある）のピクセルにする
        var image = source.Image;
        var ratioX = image.Width / (double)source.Width;
        var ratioY = image.Height / (double)source.Height;
        var subset = new SKRectI(
            Math.Clamp((int)Math.Round(crop.Left * ratioX), 0, image.Width),
            Math.Clamp((int)Math.Round(crop.Top * ratioY), 0, image.Height),
            Math.Clamp((int)Math.Round((source.Width - crop.Right) * ratioX), 0, image.Width),
            Math.Clamp((int)Math.Round((source.Height - crop.Bottom) * ratioY), 0, image.Height));
        if (subset.Width <= 0 || subset.Height <= 0)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Round(node.Opacity * 255)),
            IsAntialias = true,
        };
        if (node.Blend == BlendMode.Subtract)
        {
            paint.Blender = SubtractBlender.Value;
        }
        else
        {
            paint.BlendMode = ToSkia(node.Blend);
        }

        var destination = new SKRect(
            (float)(-visibleWidth / 2), (float)(-visibleHeight / 2), (float)(visibleWidth / 2), (float)(visibleHeight / 2));

        fixed (byte* pixels = image.Pixels)
        {
            // 画素を写さずに、切り抜いた部分だけを Skia の画像として扱う
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var whole = new SKPixmap(info, (nint)pixels, image.Stride);
            using var part = whole.ExtractSubset(subset);
            using var skImage = SKImage.FromPixels(part);

            canvas.Save();
            canvas.SetMatrix(matrix);
            canvas.DrawImage(skImage, destination, Sampling, paint);
            canvas.Restore();
        }
    }

    // Matrix3x2 は行ベクトル（x' = x·M11 + y·M21 + M31）、SKMatrix は列ベクトル（x' = ScaleX·x + SkewX·y + TransX）
    private static SKMatrix ToSkia(Matrix3x2 m) =>
        new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    private static SKBlendMode ToSkia(BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Add => SKBlendMode.Plus,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Difference => SKBlendMode.Difference,
        _ => SKBlendMode.SrcOver,
    };
}
