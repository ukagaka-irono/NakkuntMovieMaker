using NakkuntMovieMaker.Rendering.Effects;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

// 形・位置・不透明度を変えるエフェクト

/// <summary>
/// モザイク。ブロック（ローカル座標の原点からの格子）ごとに色を平均する。
/// 格子をローカル座標に固定するので、画像が広がってもブロックの位置は動かない
/// </summary>
internal sealed unsafe class MosaicEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Mosaic.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var size = p.GetFloat("size") * input.Scale;
        if (size <= 1)
        {
            return input;
        }

        var pixels = input.Pixels;
        var rowBytes = input.RowBytes;
        var bounds = input.PixelBounds;
        var firstRow = (int)Math.Floor(bounds.Top / size);
        var lastRow = (int)Math.Floor((bounds.Bottom - 1) / size);
        var firstColumn = (int)Math.Floor(bounds.Left / size);
        var lastColumn = (int)Math.Floor((bounds.Right - 1) / size);
        Parallel.For(firstRow, lastRow + 1, blockY =>
        {
            var top = Math.Max(bounds.Top, (int)Math.Round(blockY * size)) - bounds.Top;
            var bottom = Math.Min(bounds.Bottom, (int)Math.Round((blockY + 1) * size)) - bounds.Top;
            for (var blockX = firstColumn; blockX <= lastColumn; blockX++)
            {
                var left = Math.Max(bounds.Left, (int)Math.Round(blockX * size)) - bounds.Left;
                var right = Math.Min(bounds.Right, (int)Math.Round((blockX + 1) * size)) - bounds.Left;
                if (right <= left || bottom <= top)
                {
                    continue;
                }

                long b = 0, g = 0, r = 0, a = 0;
                for (var y = top; y < bottom; y++)
                {
                    var row = pixels + y * rowBytes;
                    for (var x = left; x < right; x++)
                    {
                        b += row[x * 4];
                        g += row[x * 4 + 1];
                        r += row[x * 4 + 2];
                        a += row[x * 4 + 3];
                    }
                }

                var count = (right - left) * (bottom - top);
                var average = (B: (byte)((b + count / 2) / count), G: (byte)((g + count / 2) / count), R: (byte)((r + count / 2) / count), A: (byte)((a + count / 2) / count));
                for (var y = top; y < bottom; y++)
                {
                    var row = pixels + y * rowBytes;
                    for (var x = left; x < right; x++)
                    {
                        row[x * 4] = average.B;
                        row[x * 4 + 1] = average.G;
                        row[x * 4 + 2] = average.R;
                        row[x * 4 + 3] = average.A;
                    }
                }
            }
        });
        return input;
    }
}

/// <summary>
/// マスク（図形）。図形の内側だけを残す（反転すると外側だけを残す）。図形の位置はローカル座標で、境界はぼかせる
/// </summary>
internal sealed class MaskEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Mask.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        using var mask = input.CreateLike();
        using (var canvas = mask.OpenLocalCanvas())
        {
            canvas.Translate(p.GetFloat("x"), p.GetFloat("y"));
            canvas.RotateDegrees(p.GetFloat("rotation"));
            var width = p.GetFloat("width");
            var height = p.GetFloat("height");
            var rect = new SKRect(-width / 2, -height / 2, width / 2, height / 2);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var sigma = RenderMath.BlurSigma(p.GetNumber("feather"), 1);
            if (sigma >= 0.05f)
            {
                // ローカル座標のキャンバスなので、σ もローカル座標で指定する（Skia が倍率を掛ける）
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
            }

            if (p.GetText("shape") == "ellipse")
            {
                canvas.DrawOval(rect, paint);
            }
            else
            {
                var radius = Math.Min(p.GetFloat("cornerRadius"), Math.Min(width, height) / 2);
                canvas.DrawRoundRect(rect, radius, radius, paint);
            }
        }

        using var output = input.OpenPixelCanvas();
        using var composite = new SKPaint { BlendMode = p.GetBool("invert") ? SKBlendMode.DstOut : SKBlendMode.DstIn };
        mask.DrawPixels(output, composite);
        return input;
    }
}

/// <summary>フェードイン・フェードアウト。定義が時間から求めた係数（alpha）を不透明度に掛ける</summary>
internal sealed class FadeEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Fade.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var alpha = p.GetFloat("alpha");
        if (alpha < 1)
        {
            PixelOps.MultiplyAlpha(input, Math.Clamp(alpha, 0, 1));
        }

        return input;
    }
}

/// <summary>振動。定義が時間から求めたずれ（offsetX・offsetY）と回転（angle）で、ローカル座標の原点のまわりに動かす</summary>
internal sealed class ShakeEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Shake.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var dx = p.GetFloat("offsetX") * input.Scale;
        var dy = p.GetFloat("offsetY") * input.Scale;
        var angle = p.GetFloat("angle");
        if (dx == 0 && dy == 0 && angle == 0)
        {
            return input;
        }

        // ピクセル座標で、原点のまわりに回してからずらす
        var matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(dx, dy), SKMatrix.CreateRotationDegrees(angle));
        var bounds = matrix.MapRect(SKRect.Create(input.PixelLeft, input.PixelTop, input.Width, input.Height));
        var output = EffectImage.Create(SKRectI.Round(new SKRect(
            MathF.Floor(bounds.Left), MathF.Floor(bounds.Top), MathF.Ceiling(bounds.Right), MathF.Ceiling(bounds.Bottom))), input.Scale);
        using var canvas = output.OpenPixelCanvas();
        canvas.Concat(in matrix);
        using var paint = new SKPaint { IsAntialias = true };
        using var image = input.AsImage();
        canvas.DrawImage(image, SKRect.Create(input.PixelLeft, input.PixelTop, input.Width, input.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
        return output;
    }
}
