using System.Numerics;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Color;

/// <summary>premultiplied の BGRA（8 ビット）の画像に、色の変換（ColorLut）をかける</summary>
public static class ColorLutImage
{
    /// <summary>画像を変換した、新しい画像（元の画像は変えない）</summary>
    public static BgraImage Apply(ColorLut lut, BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(lut);
        ArgumentNullException.ThrowIfNull(image);
        var output = new BgraImage(image.Width, image.Height, image.Pixels.ToArray());
        ApplyInPlace(lut, output);
        return output;
    }

    /// <summary>
    /// 画像をその場で変換する。透明でない画素は、いったん straight にして変換し、premultiplied に戻す（8 ビットに四捨五入）
    /// </summary>
    public static void ApplyInPlace(ColorLut lut, BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(lut);
        ArgumentNullException.ThrowIfNull(image);
        var pixels = image.Pixels;
        var stride = image.Stride;
        var width = image.Width;
        Parallel.For(0, image.Height, y =>
        {
            var row = pixels.AsSpan(y * stride, width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                var alpha = row[x + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var a = alpha / 255f;
                var color = lut.Sample(new Vector3(row[x + 2], row[x + 1], row[x]) / 255f / a) * a * 255f;
                row[x] = (byte)Math.Clamp(MathF.Round(color.Z), 0, alpha);
                row[x + 1] = (byte)Math.Clamp(MathF.Round(color.Y), 0, alpha);
                row[x + 2] = (byte)Math.Clamp(MathF.Round(color.X), 0, alpha);
            }
        });
    }
}
