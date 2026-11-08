using System.Numerics;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Color;
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

    // エフェクトをかける画像の一辺の上限（ピクセル）
    private const float MaxEffectSize = 8192;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    // 素材・エフェクトの途中の画像は sRGB。リニアのモードでは、描く先を 16 ビット浮動小数点のリニアの sRGB（Rec.709）にし、
    // Skia に変換させる（sRGB のモードでは、描く先に色空間がないので、変換は行われない）
    internal static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
    private static readonly SKColorSpace LinearSrgb = SKColorSpace.CreateSrgbLinear();

    private static readonly Lazy<SKBlender> SubtractBlender = new(() =>
    {
        using var effect = SKRuntimeEffect.CreateBlender(SubtractSksl, out var errors)
            ?? throw new InvalidOperationException($"減算の合成を準備できませんでした: {errors}");
        return effect.ToBlender();
    });

    // エフェクトをかけた結果を覚えておく量
    private const long EffectCacheBytes = 128L << 20;

    private readonly TextLayoutCache _layouts;
    private readonly EffectResultCache _effects = new(EffectCacheBytes);

    /// <param name="fonts">使うフォント（layouts を渡したときは使わない）</param>
    /// <param name="layouts">文字の配置のキャッシュ（大きさを測る処理などと共有するとき）</param>
    public SkiaCpuBackend(FontProvider? fonts = null, TextLayoutCache? layouts = null)
    {
        _layouts = layouts ?? new TextLayoutCache(fonts);
    }

    public RenderBackendInfo Info { get; } = new(RenderBackendKind.Cpu, "Skia（CPU）", null, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    public RenderResult Render(FramePlan plan, IMediaSource media, RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CpuRenderResult(Render(plan, media, request.Scale, cancellationToken));
    }

    /// <summary>plan を描いて、BGRA の画像として返す（CPU 版なので、そのまま返せる）</summary>
    public BgraImage Render(FramePlan plan, IMediaSource media, double scale = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        if (!(scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "倍率は 0 より大きくしてください");
        }

        var (width, height) = OutputSize(plan, scale);
        var image = RenderInto(plan.Nodes, plan, media, scale, new PixelRegion(0, 0, width, height), plan.Background, cancellationToken);
        if (plan.Color.Output is { } output)
        {
            // 出力の変換（プレビューの表示・書き出し）は、描き終えた sRGB の画像にかける
            ColorLutImage.ApplyInPlace(output, image);
        }

        return image;
    }

    /// <summary>出力の大きさ（GPU 版も同じ大きさにする）</summary>
    public static (int Width, int Height) OutputSize(FramePlan plan, double scale) =>
        (Math.Max(1, (int)Math.Round(plan.Width * scale)), Math.Max(1, (int)Math.Round(plan.Height * scale)));

    /// <summary>
    /// ノード 1 つが描かれる範囲（出力の画素。出力の中に収める）。描くものがなければ null。
    /// エフェクト（外側に広がる）とトランジションは、出力の全体にする
    /// </summary>
    public PixelRegion? NodeRegion(RenderNode node, FramePlan plan, IMediaSource media, double scale)
    {
        ArgumentNullException.ThrowIfNull(node);
        var (width, height) = OutputSize(plan, scale);
        if (node is not DrawNode { Effects.IsEmpty: true } draw)
        {
            return new PixelRegion(0, 0, width, height);
        }

        var matrix = SKMatrix.Concat(OutputScale(plan, width, height), ToSkia(draw.Transform));
        if (CreateContent(draw, media, plan, Hint(matrix)) is not { } content)
        {
            return null;
        }

        // 縁のアンチエイリアスと、線の角のはみ出しの分（4 画素）を足す
        var bounds = matrix.MapRect(content.Bounds);
        var x0 = Math.Max(0, (int)Math.Floor(bounds.Left) - 4);
        var y0 = Math.Max(0, (int)Math.Floor(bounds.Top) - 4);
        var x1 = Math.Min(width, (int)Math.Ceiling(bounds.Right) + 4);
        var y1 = Math.Min(height, (int)Math.Ceiling(bounds.Bottom) + 4);
        return x1 > x0 && y1 > y0 ? new PixelRegion(x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary>
    /// ノード 1 つを、出力の region の範囲だけ、透明な画像に描く（全体を描いたときと同じ画素の並び）。GPU 版が、まだ GPU で描けないものに使う
    /// </summary>
    public BgraImage RenderRegion(RenderNode node, FramePlan plan, IMediaSource media, double scale, PixelRegion region, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        return RenderInto([node], plan, media, scale, region, RgbaColor.Transparent, cancellationToken);
    }

    /// <summary>
    /// ノード 1 つを、出力の region の範囲だけ、渡されたキャンバスに描く（GPU 版が、Skia の GPU 版の面に文字や図形を描くのに使う）。
    /// キャンバスは region の大きさで、左上が region の左上。消すのは呼ぶ側が行う
    /// </summary>
    public void DrawRegion(SKCanvas canvas, RenderNode node, FramePlan plan, IMediaSource media, double scale, PixelRegion region)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(plan);
        var (width, height) = OutputSize(plan, scale);
        var outputScale = SKMatrix.Concat(SKMatrix.CreateTranslation(-region.X, -region.Y), OutputScale(plan, width, height));
        canvas.Save();
        canvas.Translate(-region.X, -region.Y);
        DrawNode(canvas, node, media, outputScale, plan, WorkingInfo(plan, width, height));
        canvas.Restore();
    }

    // nodes を、出力の region の範囲だけ描く
    private BgraImage RenderInto(
        IReadOnlyList<RenderNode> nodes, FramePlan plan, IMediaSource media, double scale, PixelRegion region, RgbaColor background, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        if (!(scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "倍率は 0 より大きくしてください");
        }

        var (width, height) = OutputSize(plan, scale);
        var pixels = new byte[region.Width * region.Height * 4];
        var info = WorkingInfo(plan, region.Width, region.Height);
        var whole = WorkingInfo(plan, width, height);
        var linear = plan.Color.IsLinear;

        fixed (byte* output = pixels)
        {
            // sRGB のモードは、結果の配列に直接描く。リニアのモードは、別の画像に描いてから sRGB の 8 ビットに変換する
            using var surface = (linear ? SKSurface.Create(info) : SKSurface.Create(info, (nint)output, region.Width * 4))
                ?? throw new InvalidOperationException("描画先を準備できませんでした");
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(background.R, background.G, background.B, background.A));

            // シーンの座標 → 出力の座標。範囲だけを描くときは、範囲の左上を原点にずらす
            // （Draw は行列を置き換えるので、ずらす分を出力の行列にも入れる。トランジションの全体の画像は canvas のずれで置く）
            var outputScale = SKMatrix.Concat(SKMatrix.CreateTranslation(-region.X, -region.Y), OutputScale(plan, width, height));
            canvas.Translate(-region.X, -region.Y);
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrawNode(canvas, node, media, outputScale, plan, whole);
            }

            canvas.Flush();
            if (linear)
            {
                var srgb = new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
                if (!surface.ReadPixels(srgb, (nint)output, region.Width * 4, 0, 0))
                {
                    throw new InvalidOperationException("描いた結果を sRGB に変換できませんでした");
                }
            }
        }

        return new BgraImage(region.Width, region.Height, pixels);
    }

    // 描く先の画像の形式（sRGB のモードは 8 ビットで色空間なし、リニアのモードは 16 ビット浮動小数点のリニアの sRGB）
    private static SKImageInfo WorkingInfo(FramePlan plan, int width, int height) => plan.Color.IsLinear
        ? new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul, LinearSrgb)
        : new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

    private static SKMatrix OutputScale(FramePlan plan, int width, int height) =>
        SKMatrix.CreateScale((float)(width / (double)plan.Width), (float)(height / (double)plan.Height));

    // 素材を読む細かさ（ローカル座標 1 あたりの出力の画素）
    private static double Hint(SKMatrix matrix) => Math.Max(
        Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY),
        Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY));

    public void Dispose()
    {
        // CPU 版は、破棄するまで持ち続ける資源がない（キャッシュはメモリだけ）
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
        var hint = Hint(matrix);
        var source = node.Content is ImageContent image ? media.GetImage(image.Asset, hint) : null;
        var content = CreateContent(node, media, plan, hint, source);
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
            using var plain = composite is null ? new SKPaint { IsAntialias = true } : null;
            if (CachedEffects(content, node, source, media, (float)hint) is ({ } result, var owned))
            {
                canvas.DrawImage(result.Image, result.LocalBounds, Sampling, composite ?? plain);
                if (owned)
                {
                    result.Image.Dispose();
                }
            }
        }

        canvas.Restore();
    }

    // エフェクトをかけた結果。動画（毎フレーム中身が変わる）以外は、同じ素材・エフェクト・細かさの結果を使い回す。
    // Owned が true なら覚えていないので、描いたら破棄する
    private (EffectResult Result, bool Owned)? CachedEffects(Content content, DrawNode node, SourceImage? source, IMediaSource media, float hint)
    {
        var key = node.Content is VideoFrameContent ? null : new EffectKey(source ?? (object)node.Content, node.Crop, node.Effects, hint);
        if (key is not null && _effects.TryGet(key, out var cached))
        {
            return (cached, false);
        }

        using var image = ApplyEffects(content, node, media, hint);
        if (image is null)
        {
            return null;
        }

        // 描いている途中で変わらないよう、画素を写した変わらない画像にする
        var result = new EffectResult(SKImage.FromBitmap(image.Bitmap), image.LocalBounds);
        if (key is null)
        {
            return (result, true);
        }

        _effects.Add(key, result);
        return (result, false);
    }

    /// <summary>
    /// エフェクトをかける前の画像（中身を、出力と同じくらいの細かさで描いたもの）。GPU 版が、エフェクトを GPU でかけるときに使う。
    /// 描くものがなければ null
    /// </summary>
    public EffectSource? RasterizeForEffects(DrawNode node, FramePlan plan, IMediaSource media, double scale)
    {
        ArgumentNullException.ThrowIfNull(node);
        var (width, height) = OutputSize(plan, scale);
        var hint = Hint(SKMatrix.Concat(OutputScale(plan, width, height), ToSkia(node.Transform)));
        if (CreateContent(node, media, plan, hint) is not { } content)
        {
            return null;
        }

        using var image = CreateEffectImage(content, (float)hint);
        if (image is null)
        {
            return null;
        }

        var pixels = new byte[image.Width * image.Height * 4];
        var row = image.Width * 4;
        for (var y = 0; y < image.Height; y++)
        {
            new ReadOnlySpan<byte>(image.Pixels + y * image.RowBytes, row).CopyTo(pixels.AsSpan(y * row, row));
        }

        return new EffectSource(new BgraImage(image.Width, image.Height, pixels), image.PixelLeft, image.PixelTop, image.Scale);
    }

    // 中身を、出力と同じくらいの細かさ（ローカル座標 1 あたり scale ピクセル）の画像にする（エフェクトをかける前）
    private static EffectImage? CreateEffectImage(Content content, float scale)
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

        return image;
    }

    // 中身を画像にしてから、エフェクトを順にかける
    private static EffectImage? ApplyEffects(Content content, DrawNode node, IMediaSource media, float scale)
    {
        var image = CreateEffectImage(content, scale);
        if (image is null)
        {
            return null;
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

    // 描く中身（ローカル座標）。静止画は、読んだ素材を source で渡せる
    private Content? CreateContent(DrawNode node, IMediaSource media, FramePlan plan, double hint, SourceImage? source = null) => node.Content switch
    {
        VideoFrameContent video => media.GetVideoFrame(video.Asset, video.SourceTime, hint) is { } frame ? ImageContent(node.Crop, frame) : null,
        ImageContent image => (source ?? media.GetImage(image.Asset, hint)) is { } picture ? ImageContent(node.Crop, picture) : null,
        TextContent text => TextContent(node.Crop, text),
        ShapeContent shape => ShapeContent(node.Crop, shape.Shape, plan),
        TachieContent tachie => TachieContent(node.Crop, tachie, media, hint),
        _ => null,
    };

    private TextLayout GetLayout(string text, TextStyle style) => _layouts.Get(text, style);

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
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
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
                var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
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

/// <summary>
/// エフェクトをかける前の画像。Image の左上は、ピクセル座標（ローカル座標を Scale 倍したもの）の (PixelLeft, PixelTop)
/// </summary>
public sealed record EffectSource(BgraImage Image, int PixelLeft, int PixelTop, float Scale);
