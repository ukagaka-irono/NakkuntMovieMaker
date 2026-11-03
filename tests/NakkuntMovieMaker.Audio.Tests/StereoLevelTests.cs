namespace NakkuntMovieMaker.Audio.Tests;

public sealed class StereoLevelTests
{
    [Fact]
    public void MeasuresEachChannel()
    {
        // 左は振幅 0.5 のサイン波、右は無音
        var stereo = Enumerable.Range(0, 4800).SelectMany(i => new[] { (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000)), 0f }).ToArray();

        var level = StereoLevel.Measure(stereo);

        Assert.Equal(-6.02, level.Left.PeakDb, 2);
        Assert.Equal(-9.03, level.Left.RmsDb, 2);
        Assert.Equal(Loudness.Silence, level.Right.PeakDb);
        Assert.Equal(StereoLevel.Silent, StereoLevel.Measure([]));
    }
}
