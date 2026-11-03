using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Tests;

internal static class Pixels
{
    /// <summary>(x, y) の色が、各チャンネル tolerance 以内で expected（premultiplied の R, G, B, A）と同じか</summary>
    public static void AssertColor(BgraImage image, int x, int y, (int R, int G, int B, int A) expected, int tolerance = 1)
    {
        var (b, g, r, a) = image.GetPixel(x, y);
        var ok = Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance
            && Math.Abs(b - expected.B) <= tolerance && Math.Abs(a - expected.A) <= tolerance;
        Assert.True(ok, $"({x}, {y}) の色: 期待 {expected}、実際 ({r}, {g}, {b}, {a})");
    }

    /// <summary>背景（color）と違う色の画素の数</summary>
    public static int CountDifferent(BgraImage image, (byte R, byte G, byte B) color)
    {
        var count = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] != color.B || image.Pixels[i + 1] != color.G || image.Pixels[i + 2] != color.R)
            {
                count++;
            }
        }

        return count;
    }
}
