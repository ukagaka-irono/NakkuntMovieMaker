using System.Numerics;

namespace NakkuntMovieMaker.Media;

/// <summary>平面のまま渡す映像の形式（どちらも 4:2:0。色差は Cb・Cr を交互に並べた 1 つの平面）</summary>
public enum VideoPlaneFormat
{
    /// <summary>8 ビット（1 標本 1 バイト）</summary>
    Nv12,

    /// <summary>10 ビット（1 標本 2 バイトのリトルエンディアンで、値は上位の 10 ビット）</summary>
    P010,
}

/// <summary>
/// デコードした映像を、RGB にせずに YUV の平面のまま持つもの（GPU で RGB にするため）。
/// 色差の平面の大きさは、輝度の半分（切り上げ）。Color の行列・範囲・色差の位置で RGB にする
/// </summary>
public sealed class VideoPlanes
{
    public VideoPlanes(VideoPlaneFormat format, int width, int height, byte[] luma, int lumaStride, byte[] chroma, int chromaStride, VideoColorInfo color)
    {
        ArgumentNullException.ThrowIfNull(luma);
        ArgumentNullException.ThrowIfNull(chroma);
        ArgumentNullException.ThrowIfNull(color);
        var bytes = format == VideoPlaneFormat.P010 ? 2 : 1;
        if (width <= 0 || height <= 0 || lumaStride < width * bytes || chromaStride < ChromaSize(width) * 2 * bytes
            || luma.Length < lumaStride * height || chroma.Length < chromaStride * ChromaSize(height))
        {
            throw new ArgumentException("平面の大きさが合いません");
        }

        Format = format;
        Width = width;
        Height = height;
        Luma = luma;
        LumaStride = lumaStride;
        Chroma = chroma;
        ChromaStride = chromaStride;
        Color = color;
    }

    public VideoPlaneFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>輝度（Y）の平面</summary>
    public byte[] Luma { get; }

    /// <summary>輝度の 1 行のバイト数</summary>
    public int LumaStride { get; }

    /// <summary>色差（Cb・Cr を交互に並べたもの）の平面</summary>
    public byte[] Chroma { get; }

    /// <summary>色差の 1 行のバイト数</summary>
    public int ChromaStride { get; }

    public VideoColorInfo Color { get; }

    public int ChromaWidth => ChromaSize(Width);

    public int ChromaHeight => ChromaSize(Height);

    /// <summary>1 標本のビット数</summary>
    public int BitDepth => Format == VideoPlaneFormat.P010 ? 10 : 8;

    /// <summary>平面の合計のバイト数</summary>
    public long Bytes => Luma.LongLength + Chroma.LongLength;

    private static int ChromaSize(int size) => (size + 1) / 2;
}

/// <summary>
/// YUV → RGB の変換（CPU）。GPU のシェーダー（yuv_to_work.slang）と同じ式で計算する。
/// 色差は、色差の位置（左・中央・左上）に合わせて縦横に線形に補間する
/// </summary>
public static class YuvConversion
{
    /// <summary>行列の係数（Kr, Kb）。分からない行列は、高さで推測した行列にする</summary>
    public static (float Kr, float Kb) Coefficients(VideoMatrix matrix, int height) => matrix switch
    {
        VideoMatrix.Bt709 => (0.2126f, 0.0722f),
        VideoMatrix.Bt470BG or VideoMatrix.Smpte170M => (0.299f, 0.114f),
        VideoMatrix.Smpte240M => (0.212f, 0.087f),
        VideoMatrix.Fcc => (0.30f, 0.11f),
        VideoMatrix.Bt2020Ncl or VideoMatrix.Bt2020Cl => (0.2627f, 0.0593f),
        _ => Coefficients(VideoColorInfo.GuessMatrix(height), height),
    };

    /// <summary>
    /// 標本の値（0〜2^bits − 1）→ 0〜1 の Y と −0.5〜0.5 の Cb・Cr にする式：y = (Y − yOffset) × yScale、c = (C − cOffset) × cScale
    /// </summary>
    public static (float YOffset, float YScale, float COffset, float CScale) Range(VideoColorRange range, int bits)
    {
        var unit = (float)(1 << (bits - 8));
        var max = (1 << bits) - 1f;
        return range == VideoColorRange.Full
            ? (0, 1 / max, 128 * unit, 1 / max)
            : (16 * unit, 1 / (219 * unit), 128 * unit, 1 / (224 * unit));
    }

    /// <summary>色差の標本の位置（輝度の画素の番号で、色差の標本 0 の位置）</summary>
    public static (float X, float Y) ChromaOrigin(VideoChromaLocation location) => location switch
    {
        VideoChromaLocation.Center => (0.5f, 0.5f),
        VideoChromaLocation.TopLeft => (0, 0),
        VideoChromaLocation.Top => (0.5f, 0),
        VideoChromaLocation.BottomLeft => (0, 1),
        VideoChromaLocation.Bottom => (0.5f, 1),
        _ => (0, 0.5f), // 左（MPEG-2・H.264・HEVC の既定。位置の指定がないときも）
    };

    /// <summary>平面を BGRA（不透明、8 ビットに四捨五入）にする。原色と伝達特性は変えない</summary>
    public static BgraImage ToBgra(VideoPlanes planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        var (kr, kb) = Coefficients(planes.Color.Matrix, planes.Height);
        var (yOffset, yScale, cOffset, cScale) = Range(planes.Color.Range, planes.BitDepth);
        var (originX, originY) = ChromaOrigin(planes.Color.ChromaLocation);
        var width = planes.Width;
        var pixels = new byte[width * planes.Height * 4];
        Parallel.For(0, planes.Height, y =>
        {
            var row = pixels.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var luma = (Sample(planes, planes.Luma, planes.LumaStride, x, y) - yOffset) * yScale;
                var (cb, cr) = Chroma(planes, (x - originX) / 2, (y - originY) / 2);
                var rgb = ToRgb(luma, (cb - cOffset) * cScale, (cr - cOffset) * cScale, kr, kb);
                row[x * 4] = Unorm8(rgb.Z);
                row[x * 4 + 1] = Unorm8(rgb.Y);
                row[x * 4 + 2] = Unorm8(rgb.X);
                row[x * 4 + 3] = 255;
            }
        });
        return new BgraImage(width, planes.Height, pixels);
    }

    /// <summary>Y（0〜1）・Cb・Cr（−0.5〜0.5）→ R'G'B'</summary>
    public static Vector3 ToRgb(float y, float cb, float cr, float kr, float kb)
    {
        var r = y + 2 * (1 - kr) * cr;
        var b = y + 2 * (1 - kb) * cb;
        var g = (y - kr * r - kb * b) / (1 - kr - kb);
        return new Vector3(r, g, b);
    }

    private static byte Unorm8(float value) => (byte)MathF.Floor(Math.Clamp(value, 0, 1) * 255 + 0.5f);

    // 輝度か色差の標本の値（範囲の外は端の標本）
    private static float Sample(VideoPlanes planes, byte[] plane, int stride, int x, int y)
    {
        if (planes.Format == VideoPlaneFormat.P010)
        {
            var i = y * stride + x * 2;
            return (plane[i] | (plane[i + 1] << 8)) >> 6;
        }

        return plane[y * stride + x];
    }

    // 色差の平面の位置（u, v：色差の標本の番号、端数あり）の Cb・Cr を、線形に補間する
    private static (float Cb, float Cr) Chroma(VideoPlanes planes, float u, float v)
    {
        var maxX = planes.ChromaWidth - 1;
        var maxY = planes.ChromaHeight - 1;
        var u0 = (int)MathF.Floor(u);
        var v0 = (int)MathF.Floor(v);
        var fu = u - u0;
        var fv = v - v0;
        int Cx(int i) => Math.Clamp(i, 0, maxX) * 2;
        int Cy(int j) => Math.Clamp(j, 0, maxY);
        float At(int i, int j, int channel) => Sample(planes, planes.Chroma, planes.ChromaStride, Cx(i) + channel, Cy(j));
        float Lerp(int channel)
        {
            var top = At(u0, v0, channel) + (At(u0 + 1, v0, channel) - At(u0, v0, channel)) * fu;
            var bottom = At(u0, v0 + 1, channel) + (At(u0 + 1, v0 + 1, channel) - At(u0, v0 + 1, channel)) * fu;
            return top + (bottom - top) * fv;
        }

        return (Lerp(0), Lerp(1));
    }
}
