namespace NakkuntMovieMaker.Core.Time;

/// <summary>
/// フレーム・音声サンプル・時刻の変換。
/// フレームは「そのシーンのフレームレートでの整数番号」、サンプルは「サンプルレートでの整数番号」で表す。
/// 計算はすべて整数で行い、長い動画でも誤差が積み重ならないようにする
/// </summary>
public static class TimeMath
{
    /// <summary>フレームの開始時刻（秒）</summary>
    public static Rational FrameToSeconds(long frame, Rational fps)
    {
        ValidateFps(fps);
        return frame * fps.Reciprocal();
    }

    /// <summary>時刻（秒）を含むフレームの番号。丸め方を変えると「最も近いフレーム」なども求められる</summary>
    public static long SecondsToFrame(Rational seconds, Rational fps, Rounding rounding = Rounding.Down)
    {
        ValidateFps(fps);
        return (seconds * fps).ToInt64(rounding);
    }

    /// <summary>フレームの開始位置にあたるサンプルの番号（そのフレームに属する最初のサンプル）</summary>
    public static long FrameToSample(long frame, Rational fps, int sampleRate)
    {
        ValidateFps(fps);
        ValidateSampleRate(sampleRate);
        // frame / fps 秒 × sampleRate を切り上げると、フレームの開始時刻以降の最初のサンプルになる
        return Rational.DivideRounded((Int128)frame * sampleRate * fps.Denominator, fps.Numerator, Rounding.Up);
    }

    /// <summary>サンプルが属するフレームの番号</summary>
    public static long SampleToFrame(long sample, Rational fps, int sampleRate)
    {
        ValidateFps(fps);
        ValidateSampleRate(sampleRate);
        return Rational.DivideRounded((Int128)sample * fps.Numerator, (Int128)sampleRate * fps.Denominator, Rounding.Down);
    }

    /// <summary>フレーム範囲 [startFrame, startFrame + frameCount) に属するサンプルの数</summary>
    public static long FramesToSampleCount(long startFrame, long frameCount, Rational fps, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        return FrameToSample(startFrame + frameCount, fps, sampleRate) - FrameToSample(startFrame, fps, sampleRate);
    }

    /// <summary>フレームの開始時刻を TimeSpan で返す（100 ナノ秒単位に切り捨て）</summary>
    public static TimeSpan FrameToTimeSpan(long frame, Rational fps)
    {
        ValidateFps(fps);
        var ticks = Rational.DivideRounded((Int128)frame * TimeSpan.TicksPerSecond * fps.Denominator, fps.Numerator, Rounding.Down);
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>TimeSpan の時刻を含むフレームの番号</summary>
    public static long TimeSpanToFrame(TimeSpan time, Rational fps, Rounding rounding = Rounding.Down)
    {
        ValidateFps(fps);
        return Rational.DivideRounded((Int128)time.Ticks * fps.Numerator, (Int128)TimeSpan.TicksPerSecond * fps.Denominator, rounding);
    }

    private static void ValidateFps(Rational fps)
    {
        if (fps <= Rational.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "フレームレートは正の数にしてください");
        }
    }

    private static void ValidateSampleRate(int sampleRate) =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
}
