namespace NakkuntMovieMaker.Media;

/// <summary>
/// BGRA（各 8 ビット、premultiplied alpha）の画像。1 行は Width × 4 バイトで、行の間に余白はない
/// </summary>
public sealed class BgraImage
{
    public BgraImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException("画素の数が幅 × 高さと合いません", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    /// <summary>(x, y) の画素を (B, G, R, A) で返す</summary>
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        var offset = y * Stride + x * 4;
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
    }
}
