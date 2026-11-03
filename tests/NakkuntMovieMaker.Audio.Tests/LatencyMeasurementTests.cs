namespace NakkuntMovieMaker.Audio.Tests;

public class LatencyMeasurementTests
{
    [Fact]
    public void FindsTheDelayOfTheClicks()
    {
        var clicks = LatencyMeasurement.CreateClicks(4, 12000);
        var random = new Random(7);

        // 1234 サンプル遅れ、小さくなってノイズの乗ったクリック
        var recorded = new float[clicks.Length + 6000];
        for (var i = 0; i < recorded.Length; i++)
        {
            recorded[i] = (float)((random.NextDouble() * 2 - 1) * 0.01);
        }

        for (var i = 0; i < clicks.Length; i++)
        {
            recorded[i + 1234] += clicks[i] * 0.3f;
        }

        Assert.Equal(1234, LatencyMeasurement.FindDelay(clicks, recorded, 4800));
    }

    [Fact]
    public void ReturnsNullWhenTheClicksAreNotHeard()
    {
        var clicks = LatencyMeasurement.CreateClicks(4, 12000);
        var random = new Random(3);
        var noise = Enumerable.Range(0, clicks.Length + 6000).Select(_ => (float)((random.NextDouble() * 2 - 1) * 0.2)).ToArray();

        Assert.Null(LatencyMeasurement.FindDelay(clicks, new float[clicks.Length + 6000], 4800));
        Assert.Null(LatencyMeasurement.FindDelay(clicks, noise, 4800));
    }
}
