using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.TestSupport;

/// <summary>2 枚の画像の違い。MaxDifference は各チャンネルの差の最大、Psnr は PSNR（dB。同じなら無限大）、DifferentChannels は差が 2 を超えたチャンネルの数</summary>
public sealed record ImageDifference(int MaxDifference, double Psnr, long DifferentChannels)
{
    public override string ToString() => $"最大の差 {MaxDifference}、PSNR {Psnr:F1}dB、差が 2 を超えたチャンネル {DifferentChannels}";
}

/// <summary>画像を比べる（ゴールデン画像との比較と、CPU 版と GPU 版の結果の比較で共通に使う）</summary>
public static class ImageCompare
{
    /// <summary>premultiplied の BGRA の画素を、チャンネルごとに比べる。大きさが違えば失敗にする</summary>
    public static ImageDifference Compare(BgraImage expected, BgraImage actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var maxDifference = 0;
        var squaredError = 0.0;
        var different = 0L;
        for (var i = 0; i < actual.Pixels.Length; i++)
        {
            var difference = Math.Abs(actual.Pixels[i] - expected.Pixels[i]);
            maxDifference = Math.Max(maxDifference, difference);
            squaredError += difference * difference;
            if (difference > 2)
            {
                different++;
            }
        }

        var psnr = squaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squaredError / actual.Pixels.Length));
        return new ImageDifference(maxDifference, psnr, different);
    }
}
