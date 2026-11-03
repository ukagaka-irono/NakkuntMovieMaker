using System.Collections.Concurrent;
using System.Numerics;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia.Effects;
using NakkuntMovieMaker.Rendering.Skia.Transitions;
using NakkuntMovieMaker.Text;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia;

/// <summary>
/// Skia の CPU 描画で FramePlan を実行する。複数のスレッドから同時に
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

    // 文字の配置は時間がかかるので、同じ文字とスタイルの配置は使い回す（増えすぎたら捨てる）
    private const int LayoutCacheCapacity = 512;

    // エフェクトをかける画像の一辺の上限（ピクセル）
    private const float MaxEffectSize = 8192;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static readonly Lazy<SKBlender> SubtractBlender = new(() =>
    {
        using var effect = SKRuntimeEffect.CreateBlender(SubtractSksl, out var errors)
            ?? throw new InvalidOperationException($"減算の合成を準備できませんでした: {errors}");
        return effect.ToBlender();
    });

    private readonly FontProvider _fonts;
    private readonly ConcurrentDictionary<(string Text, TextStyle Style), TextLayout> _layouts = new();

    public SkiaCpuBackend(FontProvider? fonts = null)
    {
        _fonts = fonts ?? FontProvider.Default;
    }

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
                DrawNode(canvas, node, media, outputScale, plan, info);
            }

            canvas.Flush();
        }

        return new BgraImage(width, height, pixels);
    }

    private void DrawNode(SKCanvas canvas, RenderNode node, IMediaSource media, SKMatrix outputScale, FramePlan plan, SKImageInfo output)
    {
        switch (node)
        {
            case DrawNode draw:
                Draw(canvas, draw, media, outputScale, plan);
                break;
            case TransitionNode transition:
                DrawTransition(canvas, transition, media, outputScale, plan, output);
                break;
        }
    }

    // 前後のアイテムを、それぞれ出力と同じ大きさの透明な画像に描いてから、トランジションで重ねる。
    // 知らない種類のトランジションは、半分のところで切り替える
    private void DrawTransition(SKCanvas canvas, TransitionNode node, IMediaSource media, SKMatrix outputScale, FramePlan plan, SKImageInfo output)
    {
        using var from = RenderAlone(node.From, media, outputScale, plan, output);
        using var to = RenderAlone(node.To, media, outputScale, plan, output);
        var frames = new TransitionFrames(from, to, output.Width, output.Height);
        var progress = (float)Math.Clamp(node.Progress, 0, 1);
        if (CpuTransitions.Find(node.Type) is { } processor)
        {
            processor.Draw(canvas, frames, progress, node.Parameters);
        }
        else
        {
            TransitionFrames.Draw(canvas, progress < 0.5f ? from : to, SKMatrix.Identity);
        }
    }

    private SKImage? RenderAlone(RenderNode? node, IMediaSource media, SKMatrix outputScale, FramePlan plan, SKImageInfo output)
    {
        if (node is null)
        {
            return null;
        }

        using var surface = SKSurface.Create(output) ?? throw new InvalidOperationException("描画先を準備できませんでした");
        surface.Canvas.Clear(SKColors.Transparent);
        DrawNode(surface.Canvas, node, media, outputScale, plan, output);
        return surface.Snapshot();
    }

    private void Draw(SKCanvas canvas, DrawNode node, IMediaSource media, SKMatrix outputScale, FramePlan plan)
    {
        var matrix = SKMatrix.Concat(outputScale, ToSkia(node.Transform));
        var hint = Math.Max(
            Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY),
            Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY));

        var content = node.Content switch
        {
            VideoFrameContent video => media.GetVideoFrame(video.Asset, video.SourceTime, hint) is { } frame ? ImageContent(node.Crop, frame) : null,
            ImageContent image => media.GetImage(image.Asset, hint) is { } picture ? ImageContent(node.Crop, picture) : null,
            TextContent text => TextContent(node.Crop, text),
            ShapeContent shape => ShapeContent(node.Crop, shape.Shape, plan),
            TachieContent tachie => TachieContent(node.Crop, tachie, media, hint),
            _ => null,
        };
        if (content is null)
        {
            return;
        }

        var layered = node.Opacity < 1 || node.Blend != BlendMode.Normal;
        using var composite = layered ? CreatePaint(node) : null;
        canvas.Save();
        canvas.SetMatrix(matrix);
        if (node.Effects.IsEmpty)
        {
            content.Draw(canvas, composite);
        }
        else
        {
            using var result = ApplyEffects(content, node, media, (float)hint);
            if (result is not null)
            {
                using var plain = composite is null ? new SKPaint { IsAntialias = true } : null;
                result.DrawLocal(canvas, composite ?? plain);
            }
        }

        canvas.Restore();
    }

    // 中身を、出力と同じくらいの細かさ（ローカル座標 1 あたり scale ピクセル）の画像にしてから、エフェクトを順にかける
    private static EffectImage? ApplyEffects(Content content, DrawNode node, IMediaSource media, float scale)
    {
        if (content.Bounds.Width <= 0 || content.Bounds.Height <= 0)
        {
            return null;
        }

        // 大きくなりすぎないよう、一辺を MaxEffectSize ピクセルまでにする
        var longest = Math.Max(content.Bounds.Width, content.Bounds.Height);
        scale = Math.Clamp(scale, 1f / 64, Math.Max(1f / 64, Math.Min(4f, MaxEffectSize / longest)));

        var image = EffectImage.Create(content.Bounds, scale);
        using (var canvas = image.OpenLocalCanvas())
        {
            content.Draw(canvas, null);
        }

        var context = new EffectContext(media);
        foreach (var effect in node.Effects)
        {
            if (CpuEffects.Find(effect.Type) is not { } processor)
            {
                continue;
            }

            var next = processor.Apply(image, effect.Parameters, context);
            if (!ReferenceEquals(next, image))
            {
                image.Dispose();
                image = next;
            }
        }

        return image;
    }

    private TextLayout GetLayout(string text, TextStyle style)
    {
        if (_layouts.Count >= LayoutCacheCapacity)
        {
            _layouts.Clear();
        }

        return _layouts.GetOrAdd((text, style), key => TextLayout.Create(key.Text, key.Style, _fonts));
    }

    private Content? TextContent(Crop crop, TextContent text)
    {
        var layout = GetLayout(text.Text, text.Style);
        return VectorContent(crop, layout.Width, layout.Height, layout.InkBounds, canvas => layout.Draw(canvas, text.VisibleCharacters));
    }

    private static Content? ShapeContent(Crop crop, ShapeStyle shape, FramePlan plan)
    {
        // 背景はシーン全体の大きさの四角形
        var (width, height) = shape.Kind == ShapeKind.Background
            ? (plan.Width, plan.Height)
            : ((float)shape.Width, (float)shape.Height);
        return VectorContent(crop, width, height, ShapePainter.InkBounds(shape, width, height), canvas => ShapePainter.Draw(canvas, shape, width, height));
    }

    // 立ち絵：パーツの画像を、左上をそろえて奥から順に重ねる（読めないパーツは飛ばす）
    private static Content? TachieContent(Crop crop, TachieContent tachie, IMediaSource media, double hint)
    {
        var layers = tachie.Layers.Select(path => media.GetImageFile(path, hint)).OfType<SourceImage>().ToList();
        if (layers.Count == 0)
        {
            return null;
        }

        var width = layers.Max(layer => layer.Width);
        var height = layers.Max(layer => layer.Height);
        return VectorContent(crop, width, height, new SKRect(0, 0, width, height), canvas =>
        {
            foreach (var layer in layers)
            {
                DrawSource(canvas, layer, new SKRect(0, 0, layer.Width, layer.Height), null);
            }
        });
    }

    // 素材の画像を、画素を写さずに destination へ描く
    private static void DrawSource(SKCanvas canvas, SourceImage source, SKRect destination, SKPaint? paint)
    {
        var image = source.Image;
        fixed (byte* pixels = image.Pixels)
        {
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pixmap = new SKPixmap(info, (nint)pixels, image.Stride);
            using var skImage = SKImage.FromPixels(pixmap);
            using var plain = paint is null ? new SKPaint { IsAntialias = true } : null;
            canvas.DrawImage(skImage, destination, Sampling, paint ?? plain);
        }
    }

    // 文字・図形のように、幅 × 高さの箱（左上が原点）に描くもの。切り抜いた残りの中心をローカル座標の原点に置く。
    // 縁取りや影を重ねて描くので、不透明度と合成モードは、全体を描いてからまとめて適用する
    private static Content? VectorContent(Crop crop, float width, float height, SKRect inkBounds, Action<SKCanvas> draw)
    {
        var visible = new SKRect((float)crop.Left, (float)crop.Top, (float)(width - crop.Right), (float)(height - crop.Bottom));
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            return null;
        }

        var cropped = crop != Crop.None;
        var bounds = inkBounds;
        if (cropped)
        {
            bounds.Intersect(visible);
        }

        var local = bounds;
        local.Offset(-visible.MidX, -visible.MidY);
        return new Content(local, (canvas, composite) =>
        {
            canvas.Save();
            canvas.Translate(-visible.MidX, -visible.MidY);
            if (cropped)
            {
                canvas.ClipRect(visible, antialias: true);
            }

            if (composite is not null)
            {
                canvas.SaveLayer(bounds, composite);
            }

            draw(canvas);
            if (composite is not null)
            {
                canvas.Restore();
            }

            canvas.Restore();
        });
    }

    // 素材を切り抜いて、残った部分の中心をローカル座標の原点に置く
    private static Content? ImageContent(Crop crop, SourceImage source)
    {
        var visibleWidth = source.Width - crop.Left - crop.Right;
        var visibleHeight = source.Height - crop.Top - crop.Bottom;
        if (visibleWidth <= 0 || visibleHeight <= 0)
        {
            return null;
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
            return null;
        }

        var destination = new SKRect(
            (float)(-visibleWidth / 2), (float)(-visibleHeight / 2), (float)(visibleWidth / 2), (float)(visibleHeight / 2));
        return new Content(destination, (canvas, composite) =>
        {
            fixed (byte* pixels = image.Pixels)
            {
                // 画素を写さずに、切り抜いた部分だけを Skia の画像として扱う
                var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var whole = new SKPixmap(info, (nint)pixels, image.Stride);
                using var part = whole.ExtractSubset(subset);
                using var skImage = SKImage.FromPixels(part);
                using var plain = composite is null ? new SKPaint { IsAntialias = true } : null;
                canvas.DrawImage(skImage, destination, Sampling, composite ?? plain);
            }
        });
    }

    private static SKPaint CreatePaint(DrawNode node)
    {
        var paint = new SKPaint
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

        return paint;
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

    /// <summary>
    /// ローカル座標（切り抜いた残りの中心が原点）に描ける中身。Bounds は描かれる範囲。
    /// Draw の 2 つ目の引数は、不透明度と合成モード（null なら、そのまま重ねる）
    /// </summary>
    private sealed record Content(SKRect Bounds, Action<SKCanvas, SKPaint?> Draw);
}
