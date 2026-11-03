namespace NakkuntMovieMaker.Audio;

/// <summary>
/// ラウドネスの計測（ITU-R BS.1770-4・EBU R128）と正規化。
/// 音を K 特性のフィルターに通し、400ms のブロック（100ms ずつずらす）ごとの大きさを、
/// 絶対ゲート（−70 LUFS）と相対ゲート（−10 LU）で選んで平均する
/// </summary>
public static class Loudness
{
    /// <summary>無音など、測れないときの値</summary>
    public const double Silence = double.NegativeInfinity;

    private const double AbsoluteGate = -70;
    private const double RelativeGate = -10;

    /// <summary>ステレオ（インターリーブ）の音の、全体のラウドネス（LUFS）。短すぎる・無音なら Silence</summary>
    public static double Integrated(ReadOnlySpan<float> stereo, int sampleRate)
    {
        var frames = stereo.Length / 2;
        var block = (int)Math.Round(sampleRate * 0.4);
        var step = (int)Math.Round(sampleRate * 0.1);
        if (frames < block)
        {
            return Silence;
        }

        // K 特性をかけた音の 2 乗を、100ms ごとに足しておく
        var (shelf, highPass) = KWeighting(sampleRate);
        Biquad[] shelves = [new(shelf), new(shelf)];
        Biquad[] highPasses = [new(highPass), new(highPass)];
        var segments = frames / step;
        var energy = new double[segments];
        for (var i = 0; i < segments * step; i++)
        {
            double sum = 0;
            for (var channel = 0; channel < 2; channel++)
            {
                var y = highPasses[channel].Process(shelves[channel].Process(stereo[i * 2 + channel]));
                sum += y * y;
            }

            energy[i / step] += sum;
        }

        var perBlock = block / step;
        var blocks = new List<double>();
        for (var start = 0; start + perBlock <= segments; start++)
        {
            double total = 0;
            for (var k = 0; k < perBlock; k++)
            {
                total += energy[start + k];
            }

            blocks.Add(total / (perBlock * step));
        }

        static double ToLufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);

        var gated = blocks.Where(power => power > 0 && ToLufs(power) > AbsoluteGate).ToList();
        if (gated.Count == 0)
        {
            return Silence;
        }

        var threshold = ToLufs(gated.Average()) + RelativeGate;
        var kept = gated.Where(power => ToLufs(power) > threshold).ToList();
        return kept.Count == 0 ? Silence : ToLufs(kept.Average());
    }

    /// <summary>サンプルの絶対値の最大（dBFS）。無音なら Silence</summary>
    public static double SamplePeakDb(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak > 0 ? 20 * Math.Log10(peak) : Silence;
    }

    /// <summary>measured LUFS を target LUFS にするための倍率</summary>
    public static double GainTo(double measured, double target) =>
        double.IsFinite(measured) ? Math.Pow(10, (target - measured) / 20) : 1;

    /// <summary>
    /// 音全体のラウドネスを target LUFS にそろえる。ただし、サンプルの最大が peakCeilingDb を超えないよう、倍率を抑える。
    /// 掛けた倍率を返す（無音なら 1）
    /// </summary>
    public static double Normalize(Span<float> stereo, int sampleRate, double target, double peakCeilingDb = -1)
    {
        var gain = GainTo(Integrated(stereo, sampleRate), target);
        var peak = SamplePeakDb(stereo);
        if (double.IsFinite(peak))
        {
            gain = Math.Min(gain, Math.Pow(10, (peakCeilingDb - peak) / 20));
        }

        foreach (ref var sample in stereo)
        {
            sample = (float)(sample * gain);
        }

        return gain;
    }

    /// <summary>
    /// K 特性のフィルター（高域を持ち上げるシェルフと、低域を切るハイパス）の係数。
    /// 48kHz で BS.1770 の表の係数になるアナログの原形（libebur128 と同じ求め方）から、ほかのサンプルレートの係数を求める
    /// </summary>
    public static (BiquadCoefficients Shelf, BiquadCoefficients HighPass) KWeighting(int sampleRate)
    {
        // 高域シェルフ（f0 ≈ 1682Hz、約 +4dB）
        var k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
        var q = 0.7071752369554196;
        var vh = Math.Pow(10, 3.999843853973347 / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        var shelf = new BiquadCoefficients(
            (vh + vb * k / q + k * k) / a0,
            2 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2 * (k * k - 1) / a0,
            (1 - k / q + k * k) / a0);

        // ハイパス（f0 ≈ 38Hz）。分子は BS.1770 の表と同じ 1, −2, 1
        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        q = 0.5003270373238773;
        a0 = 1 + k / q + k * k;
        var highPass = new BiquadCoefficients(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        return (shelf, highPass);
    }
}

/// <summary>2 次の IIR フィルターの係数（a0 で割ったもの）</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

/// <summary>2 次の IIR フィルター（直接形 I）</summary>
internal sealed class Biquad(BiquadCoefficients c)
{
    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;

    public double Process(double x)
    {
        var y = c.B0 * x + c.B1 * _x1 + c.B2 * _x2 - c.A1 * _y1 - c.A2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return y;
    }
}
