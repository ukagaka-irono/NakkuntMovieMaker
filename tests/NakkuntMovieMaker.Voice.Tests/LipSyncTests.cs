using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Voice.Tests;

public class LipSyncTests
{
    private const int Rate = 48000;

    private static float[] Tone(double seconds, double amplitude) =>
        [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 200 * i / Rate)))];

    [Fact]
    public void OpensTheMouthOnlyWhileTalking()
    {
        // 0.5 秒の無音 → 0.5 秒の声 → 0.5 秒の無音（30fps では 15 フレームずつ）
        float[] samples = [.. new float[Rate / 2], .. Tone(0.5, 0.4), .. new float[Rate / 2]];

        var envelope = LipSync.Envelope(samples, Rate, 30);

        Assert.Equal(45, envelope.Length);
        Assert.All(envelope[..15], level => Assert.Equal(0f, level));
        Assert.All(envelope[15..30], level => Assert.InRange(level, 0.95f, 1f));
        Assert.InRange(envelope[30], 0.5f, 0.7f); // 閉じるときはゆっくり
        Assert.All(envelope[34..], level => Assert.Equal(0f, level));
    }

    [Fact]
    public void QuietPartsKeepTheMouthClosed()
    {
        // 大きい声の 5% の音（息や雑音）は 0
        float[] samples = [.. Tone(0.3, 0.02), .. Tone(0.3, 0.4)];

        var envelope = LipSync.Envelope(samples, Rate, 30);

        Assert.All(envelope[..9], level => Assert.Equal(0f, level));
        Assert.InRange(envelope[12], 0.95f, 1f);
    }

    [Fact]
    public void HandlesSilenceAndNtscFrames()
    {
        Assert.All(LipSync.Envelope(new float[Rate], Rate, 30), level => Assert.Equal(0f, level));
        Assert.Empty(LipSync.Envelope([], Rate, 30));

        // 29.97fps で 1 秒は 30 フレーム（最後のフレームは途中まで）
        Assert.Equal(30, LipSync.Envelope(Tone(1, 0.3), Rate, Rational.Fps29_97).Length);
    }
}
