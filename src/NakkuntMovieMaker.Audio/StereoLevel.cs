namespace NakkuntMovieMaker.Audio;

/// <summary>左右それぞれの音の大きさ（レベルメーター用）</summary>
public readonly record struct StereoLevel(AudioLevel Left, AudioLevel Right)
{
    public static readonly StereoLevel Silent = new(AudioLevel.Silent, AudioLevel.Silent);

    /// <summary>ステレオ（インターリーブ）の音の、左右それぞれのピークと RMS（dBFS）</summary>
    public static StereoLevel Measure(ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length / 2;
        if (frames == 0)
        {
            return Silent;
        }

        double peakLeft = 0, peakRight = 0, squaresLeft = 0, squaresRight = 0;
        for (var i = 0; i < frames; i++)
        {
            double left = stereo[i * 2];
            double right = stereo[i * 2 + 1];
            peakLeft = Math.Max(peakLeft, Math.Abs(left));
            peakRight = Math.Max(peakRight, Math.Abs(right));
            squaresLeft += left * left;
            squaresRight += right * right;
        }

        return new StereoLevel(
            new AudioLevel(ToDb(peakLeft), ToDb(Math.Sqrt(squaresLeft / frames))),
            new AudioLevel(ToDb(peakRight), ToDb(Math.Sqrt(squaresRight / frames))));
    }

    private static double ToDb(double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : Loudness.Silence;
}
