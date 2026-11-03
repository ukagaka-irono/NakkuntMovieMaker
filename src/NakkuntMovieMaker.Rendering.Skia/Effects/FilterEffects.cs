using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering.Effects;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

// ぼかしを使うエフェクト。パラメーターの「半径」は、ぼかしが届く距離（ガウスの σ の 3 倍）をローカル座標のピクセルで表す。
// 処理はピクセル座標で行い、半径にはローカル座標 → ピクセル座標の倍率を掛ける

internal static class Blurs
{
    /// <summary>半径（ローカル座標）を、ピクセル座標での σ にする</summary>
    public static float Sigma(double radius, float scale) => (float)(radius * scale / 3);

    /// <summary>σ のぼかしが届く範囲（ピクセル）</summary>
    public static int Margin(float sigma) => sigma <= 0 ? 0 : (int)Math.Ceiling(sigma * 3);

    /// <summary>画像を、まわりに広げてぼかしたもの（元の画像はそのまま）</summary>
    public static EffectImage Blurred(EffectImage input, float sigma)
    {
        var bounds = input.PixelBounds;
        bounds.Inflate(Margin(sigma), Margin(sigma));
        var blurred = EffectImage.Create(bounds, input.Scale);
        using var canvas = blurred.OpenPixelCanvas();
        using var paint = new SKPaint();
        if (sigma > 0)
        {
            paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Decal);
        }

        input.DrawPixels(canvas, paint);
        return blurred;
    }

    public static SKColor ToSkia(RgbaColor color, float opacity = 1) =>
        new(color.R, color.G, color.B, (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)));
}

/// <summary>ぼかし。画像はぼけたぶんだけ外に広がる</summary>
internal sealed class BlurEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Blur.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var sigma = Blurs.Sigma(p.GetNumber("radius"), input.Scale);
        return sigma < 0.05f ? input : Blurs.Blurred(input, sigma);
    }
}

/// <summary>シャープ（アンシャープマスク）。元の色に「元の色 − ぼかした色」を強さだけ足す</summary>
internal sealed unsafe class SharpenEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Sharpen.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var amount = p.GetFloat("amount") / 100f;
        var sigma = Blurs.Sigma(p.GetNumber("radius"), input.Scale);
        if (amount <= 0 || sigma < 0.05f)
        {
            return input;
        }

        using var blurred = Blurs.Blurred(input, sigma);
        var source = input.Pixels;
        var blur = blurred.Pixels;
        var sourceRow = input.RowBytes;
        var blurRow = blurred.RowBytes;
        var offsetX = input.PixelLeft - blurred.PixelLeft;
        var offsetY = input.PixelTop - blurred.PixelTop;
        var width = input.Width;
        Parallel.For(0, input.Height, y =>
        {
            var s = source + y * sourceRow;
            var b = blur + (y + offsetY) * blurRow + offsetX * 4;
            for (var x = 0; x < width; x++, s += 4, b += 4)
            {
                var alpha = s[3];
                for (var c = 0; c < 3; c++)
                {
                    // premultiplied なので、色は不透明度を超えないようにする
                    var value = s[c] + amount * (s[c] - b[c]);
                    s[c] = (byte)Math.Clamp(value + 0.5f, 0, alpha);
                }
            }
        });
        return input;
    }
}

/// <summary>ドロップシャドウ。影は元の画像の形をずらしてぼかしたもので、元の画像の下に描く</summary>
internal sealed class DropShadowEffect : ICpuEffect
{
    public string Type => BuiltInEffects.DropShadow.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var dx = p.GetFloat("offsetX") * input.Scale;
        var dy = p.GetFloat("offsetY") * input.Scale;
        var sigma = Blurs.Sigma(p.GetNumber("blur"), input.Scale);
        var color = Blurs.ToSkia(p.GetColor("color"), p.GetFloat("opacity") / 100f);
        if (color.Alpha == 0)
        {
            return input;
        }

        var bounds = input.PixelBounds;
        var shadow = bounds;
        shadow.Offset((int)Math.Floor(dx), (int)Math.Floor(dy));
        shadow.Inflate(Blurs.Margin(sigma) + 1, Blurs.Margin(sigma) + 1);
        bounds.Union(shadow);

        var output = EffectImage.Create(bounds, input.Scale);
        using var canvas = output.OpenPixelCanvas();
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateDropShadow(dx, dy, sigma, sigma, color) };
        input.DrawPixels(canvas, paint);
        return output;
    }
}

/// <summary>
/// 発光。ぼかした画像（色を指定したときはその色に塗ったもの）を、スクリーンで重ねる。
/// 強さが 100% を超えるときは、何回か重ねる
/// </summary>
internal sealed class GlowEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Glow.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var strength = p.GetFloat("strength") / 100f;
        var sigma = Blurs.Sigma(p.GetNumber("radius"), input.Scale);
        if (strength <= 0 || sigma < 0.05f)
        {
            return input;
        }

        using var glow = Blurs.Blurred(input, sigma);
        if (p.GetBool("tint"))
        {
            using var tintCanvas = glow.OpenPixelCanvas();
            tintCanvas.DrawColor(Blurs.ToSkia(p.GetColor("color")), SKBlendMode.SrcIn);
        }

        var output = EffectImage.Create(glow.PixelBounds, input.Scale);
        using var canvas = output.OpenPixelCanvas();
        input.DrawPixels(canvas, null);
        for (var remaining = strength; remaining > 0; remaining -= 1)
        {
            using var paint = new SKPaint
            {
                BlendMode = SKBlendMode.Screen,
                Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Min(remaining, 1) * 255)),
            };
            glow.DrawPixels(canvas, paint);
        }

        return output;
    }
}

/// <summary>
/// 縁取り。不透明な部分（不透明度 50% 以上）からの距離が太さ以内の部分を、縁の色で塗って元の画像の下に描く。
/// 距離はユークリッド距離で求めるので、角も丸く縁取られる
/// </summary>
internal sealed unsafe class BorderEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Border.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var width = p.GetFloat("width") * input.Scale;
        var color = p.GetColor("color");
        if (width <= 0 || color.A == 0)
        {
            return input;
        }

        var sigma = Blurs.Sigma(p.GetNumber("blur"), input.Scale);
        var margin = (int)Math.Ceiling(width) + 1 + Blurs.Margin(sigma);
        var output = input.Expand(margin);

        // 縁の形（不透明度）を距離から作る
        var w = output.Width;
        var h = output.Height;
        var distance = DistanceTransform.SquaredDistanceToOpaque(output);
        using var border = output.CreateLike();
        var pixels = border.Pixels;
        var rowBytes = border.RowBytes;
        var premultiplied = Blurs.ToSkia(color);
        Parallel.For(0, h, y =>
        {
            var row = pixels + y * rowBytes;
            for (var x = 0; x < w; x++)
            {
                var coverage = Math.Clamp(width - MathF.Sqrt(distance[y * w + x]) + 0.5f, 0, 1) * premultiplied.Alpha / 255f;
                row[x * 4] = (byte)(premultiplied.Blue * coverage + 0.5f);
                row[x * 4 + 1] = (byte)(premultiplied.Green * coverage + 0.5f);
                row[x * 4 + 2] = (byte)(premultiplied.Red * coverage + 0.5f);
                row[x * 4 + 3] = (byte)(255 * coverage + 0.5f);
            }
        });

        var result = output.CreateLike();
        using var canvas = result.OpenPixelCanvas();
        using (var paint = new SKPaint())
        {
            if (sigma >= 0.05f)
            {
                paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Decal);
            }

            border.DrawPixels(canvas, paint);
        }

        output.DrawPixels(canvas, null);
        output.Dispose();
        return result;
    }
}

/// <summary>2 次元のユークリッド距離変換（Felzenszwalb と Huttenlocher の方法）</summary>
internal static unsafe class DistanceTransform
{
    private const float Infinity = 1e20f;

    /// <summary>各画素から、一番近い不透明な画素（不透明度 50% 以上）までの距離の 2 乗</summary>
    public static float[] SquaredDistanceToOpaque(EffectImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var grid = new float[width * height];
        var pixels = image.Pixels;
        var rowBytes = image.RowBytes;
        for (var y = 0; y < height; y++)
        {
            var row = pixels + y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                grid[y * width + x] = row[x * 4 + 3] >= 128 ? 0 : Infinity;
            }
        }

        // 列ごと、次に行ごとに 1 次元の距離変換をかける
        Parallel.For(0, width, x =>
        {
            var column = new float[height];
            for (var y = 0; y < height; y++)
            {
                column[y] = grid[y * width + x];
            }

            var result = Transform1D(column);
            for (var y = 0; y < height; y++)
            {
                grid[y * width + x] = result[y];
            }
        });
        Parallel.For(0, height, y =>
        {
            var result = Transform1D(grid.AsSpan(y * width, width).ToArray());
            result.CopyTo(grid.AsSpan(y * width, width));
        });
        return grid;
    }

    private static float[] Transform1D(float[] f)
    {
        var n = f.Length;
        var d = new float[n];
        var v = new int[n];
        var z = new float[n + 1];
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            // 放物線 q と、今いちばん手前の放物線 v[k] の交点。z[0] は -∞ なので、k は 0 より下がらない
            var s = Intersection(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, q, v[k]);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            var delta = q - v[k];
            d[q] = delta * delta + f[v[k]];
        }

        return d;
    }

    private static float Intersection(float[] f, int q, int p) =>
        (f[q] + (float)q * q - (f[p] + (float)p * p)) / (2f * q - 2f * p);
}
