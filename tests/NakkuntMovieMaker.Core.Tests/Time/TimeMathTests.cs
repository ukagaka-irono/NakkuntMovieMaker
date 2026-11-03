using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Tests.Time;

public class TimeMathTests
{
    private const int SampleRate = 48000;

    public static TheoryData<string> FrameRates => new("24", "25", "30", "60", "24000/1001", "30000/1001", "60000/1001");

    [Fact]
    public void FrameStartsAtExactSeconds()
    {
        Assert.Equal(new Rational(1001, 30000), TimeMath.FrameToSeconds(1, Rational.Fps29_97));
        Assert.Equal(new Rational(1001, 1), TimeMath.FrameToSeconds(30000, Rational.Fps29_97));
    }

    [Fact]
    public void SecondsMapToContainingFrame()
    {
        var fps = Rational.Fps29_97;

        Assert.Equal(0, TimeMath.SecondsToFrame(new Rational(1, 30), fps));
        Assert.Equal(1, TimeMath.SecondsToFrame(new Rational(1001, 30000), fps));
        Assert.Equal(30000, TimeMath.SecondsToFrame(1001, fps));
    }

    [Fact]
    public void FrameToSampleRoundsUpToFirstSampleOfFrame()
    {
        // 29.97fps の 1 フレームは 1601.6 サンプルなので、フレーム 1 は 1602 番目から始まる
        Assert.Equal(0, TimeMath.FrameToSample(0, Rational.Fps29_97, SampleRate));
        Assert.Equal(1602, TimeMath.FrameToSample(1, Rational.Fps29_97, SampleRate));
        Assert.Equal(48_048_000, TimeMath.FrameToSample(30000, Rational.Fps29_97, SampleRate));
        Assert.Equal(1600, TimeMath.FrameToSample(1, 30, SampleRate));
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void SampleAndFrameConversionsAreConsistent(string fpsText)
    {
        var fps = Rational.Parse(fpsText);

        for (long frame = -100; frame <= 20_000; frame++)
        {
            var firstSample = TimeMath.FrameToSample(frame, fps, SampleRate);
            var nextFirstSample = TimeMath.FrameToSample(frame + 1, fps, SampleRate);

            Assert.Equal(frame, TimeMath.SampleToFrame(firstSample, fps, SampleRate));
            Assert.Equal(frame, TimeMath.SampleToFrame(nextFirstSample - 1, fps, SampleRate));
            Assert.True(nextFirstSample > firstSample);
        }
    }

    [Fact]
    public void SampleCountsDoNotDriftOverLongDurations()
    {
        // 29.97fps で 1001 秒（30000 フレーム）は、ちょうど 48,048,000 サンプル
        var fps = Rational.Fps29_97;
        long total = 0;
        for (long frame = 0; frame < 30000; frame++)
        {
            var count = TimeMath.FramesToSampleCount(frame, 1, fps, SampleRate);
            Assert.InRange(count, 1601, 1602);
            total += count;
        }

        Assert.Equal(48_048_000, total);
        Assert.Equal(48_048_000, TimeMath.FramesToSampleCount(0, 30000, fps, SampleRate));
    }

    [Fact]
    public void LongDurationsDoNotOverflow()
    {
        // 24 時間・120fps・192kHz
        var fps = new Rational(120, 1);
        var frames = 24L * 60 * 60 * 120;

        Assert.Equal(24L * 60 * 60 * 192_000, TimeMath.FrameToSample(frames, fps, 192_000));
        Assert.Equal(frames, TimeMath.SampleToFrame(24L * 60 * 60 * 192_000, fps, 192_000));
        Assert.Equal(TimeSpan.FromHours(24), TimeMath.FrameToTimeSpan(frames, fps));
    }

    [Fact]
    public void TimeSpanConversions()
    {
        var fps = Rational.Fps29_97;

        Assert.Equal(TimeSpan.FromSeconds(1001), TimeMath.FrameToTimeSpan(30000, fps));
        Assert.Equal(30000, TimeMath.TimeSpanToFrame(TimeSpan.FromSeconds(1001), fps));
        Assert.Equal(29, TimeMath.TimeSpanToFrame(TimeSpan.FromSeconds(1), fps));
        Assert.Equal(30, TimeMath.TimeSpanToFrame(TimeSpan.FromSeconds(1), fps, Rounding.Nearest));
        Assert.Equal(30, TimeMath.TimeSpanToFrame(TimeSpan.FromSeconds(1), 30));
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeMath.FrameToSeconds(1, Rational.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeMath.FrameToSample(1, -30, SampleRate));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeMath.FrameToSample(1, 30, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeMath.FramesToSampleCount(0, -1, 30, SampleRate));
    }
}
