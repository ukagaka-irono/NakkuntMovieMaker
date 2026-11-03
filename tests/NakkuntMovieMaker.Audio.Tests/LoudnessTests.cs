namespace NakkuntMovieMaker.Audio.Tests;

public class LoudnessTests
{
    [Fact]
    public void KWeightingMatchesTheBs1770CoefficientsAt48kHz()
    {
        var (shelf, highPass) = Loudness.KWeighting(48000);

        // ITU-R BS.1770-4 の表 1・表 2
        Assert.Equal(1.53512485958697, shelf.B0, 1e-9);
        Assert.Equal(-2.69169618940638, shelf.B1, 1e-9);
        Assert.Equal(1.19839281085285, shelf.B2, 1e-9);
        Assert.Equal(-1.69065929318241, shelf.A1, 1e-9);
        Assert.Equal(0.73248077421585, shelf.A2, 1e-9);
        Assert.Equal((1.0, -2.0, 1.0), (highPass.B0, highPass.B1, highPass.B2));
        Assert.Equal(-1.99004745483398, highPass.A1, 1e-9);
        Assert.Equal(0.99007225036621, highPass.A2, 1e-9);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void StereoSineAtMinus23DbfsMeasuresMinus23Lufs(int rate)
    {
        // EBU Tech 3341 の最初の確認：1kHz・−23dBFS のステレオの正弦波は −23.0 LUFS（±0.1）
        var sine = Signals.Sine(1000, Math.Pow(10, -23 / 20.0), 20, rate: rate);

        Assert.Equal(-23.0, Loudness.Integrated(sine, rate), 0.1);
    }

    [Fact]
    public void RelativeGateIgnoresQuietParts()
    {
        // −23 の 10 秒と −36 の 10 秒（相対ゲートより 13dB 低いので除かれる）
        var loud = Signals.Sine(1000, Math.Pow(10, -23 / 20.0), 10);
        var quiet = Signals.Sine(1000, Math.Pow(10, -36 / 20.0), 10);

        Assert.Equal(-23.0, Loudness.Integrated([.. loud, .. quiet], Signals.Rate), 0.1);
    }

    [Fact]
    public void SilenceAndShortSoundsCannotBeMeasured()
    {
        Assert.Equal(Loudness.Silence, Loudness.Integrated(new float[48000 * 2 * 3], Signals.Rate));
        Assert.Equal(Loudness.Silence, Loudness.Integrated(Signals.Sine(1000, 0.5, 0.2), Signals.Rate));
        Assert.Equal(Loudness.Silence, Loudness.SamplePeakDb(new float[10]));
        Assert.Equal(-6.02, Loudness.SamplePeakDb([0.1f, -0.5f, 0.3f]), 0.01);
    }

    [Fact]
    public void NormalizeReachesTheTargetWithinThePeakCeiling()
    {
        var quiet = Signals.Sine(1000, Math.Pow(10, -30 / 20.0), 10);

        var gain = Loudness.Normalize(quiet, Signals.Rate, -14);

        Assert.Equal(Math.Pow(10, 16 / 20.0), gain, 0.05);
        Assert.Equal(-14.0, Loudness.Integrated(quiet, Signals.Rate), 0.1);

        // 正弦波はラウドネスと山の高さがほぼ同じなので、0 LUFS にすると山が 0dBFS になる。山が −1dBFS になるところで止める
        var limited = Signals.Sine(1000, Math.Pow(10, -30 / 20.0), 10);
        Loudness.Normalize(limited, Signals.Rate, 0, peakCeilingDb: -1);
        Assert.Equal(-1.0, Loudness.SamplePeakDb(limited), 0.01);
    }
}
