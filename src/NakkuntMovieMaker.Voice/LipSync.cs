using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Voice;

/// <summary>
/// 口パクに使う、フレームごとの声の大きさ（0〜1）。合成した声にも録音した声にも使う。
/// 大きさは、その声の中で一番大きいフレームを 1 として表し、それの 1 割に満たないフレーム（息や小さな雑音）は 0 にする。
/// 口がぱくぱくしすぎないよう、閉じるときはゆっくり下げる
/// </summary>
public static class LipSync
{
    // 一番大きいフレームに対して、これより小さいフレームは口を閉じる
    private const float Gate = 0.1f;

    // 1 フレームで下げられる量の上限（開くときはすぐに開く）
    private const float Release = 0.35f;

    /// <summary>モノラルの声 samples（sampleRate）を、fps のフレームごとの大きさにする</summary>
    public static float[] Envelope(ReadOnlySpan<float> samples, int sampleRate, Rational fps)
    {
        var frames = (int)TimeMath.SampleToFrame(Math.Max(0, samples.Length - 1), fps, sampleRate) + (samples.Length > 0 ? 1 : 0);
        var rms = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var start = (int)Math.Min(samples.Length, TimeMath.FrameToSample(frame, fps, sampleRate));
            var end = (int)Math.Min(samples.Length, TimeMath.FrameToSample(frame + 1, fps, sampleRate));
            double sum = 0;
            for (var i = start; i < end; i++)
            {
                sum += samples[i] * samples[i];
            }

            rms[frame] = end > start ? (float)Math.Sqrt(sum / (end - start)) : 0;
        }

        var max = rms.Length == 0 ? 0 : rms.Max();
        var envelope = new float[frames];
        var previous = 0f;
        for (var frame = 0; frame < frames; frame++)
        {
            var level = max > 0 ? rms[frame] / max : 0;
            level = level < Gate ? 0 : (level - Gate) / (1 - Gate);
            level = Math.Max(level, previous - Release);
            envelope[frame] = level;
            previous = level;
        }

        return envelope;
    }
}
