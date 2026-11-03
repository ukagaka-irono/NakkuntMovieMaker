using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Audio.Tests;

/// <summary>テスト用の音（インターリーブの float32）</summary>
internal static class Signals
{
    public const int Rate = 48000;

    /// <summary>frequency Hz・振幅 amplitude の正弦波を、各チャンネルに同じ位相で</summary>
    public static float[] Sine(double frequency, double amplitude, double seconds, int channels = 2, int rate = Rate)
    {
        var frames = (int)Math.Round(seconds * rate);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
            for (var channel = 0; channel < channels; channel++)
            {
                samples[i * channels + channel] = value;
            }
        }

        return samples;
    }

    /// <summary>ずっと value の音（直流。音量のかかり方を調べるのに使う）</summary>
    public static float[] Constant(float value, int frames, int channels = 2) => Enumerable.Repeat(value, frames * channels).ToArray();

    /// <summary>1 チャンネル目の、負から正に変わる回数（正弦波なら周波数 × 秒数）</summary>
    public static int RisingZeroCrossings(ReadOnlySpan<float> samples, int channels = 2)
    {
        var count = 0;
        for (var i = channels; i < samples.Length; i += channels)
        {
            if (samples[i - channels] < 0 && samples[i] >= 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>1 チャンネル目の [start, end) フレームの RMS</summary>
    public static double Rms(ReadOnlySpan<float> samples, int start, int end, int channel = 0, int channels = 2)
    {
        double sum = 0;
        for (var i = start; i < end; i++)
        {
            var value = samples[i * channels + channel];
            sum += value * value;
        }

        return Math.Sqrt(sum / (end - start));
    }
}

/// <summary>メモリ上の音を素材として渡す</summary>
internal sealed class TestAudio : IAudioSource
{
    private readonly Dictionary<AssetId, IAudioStream> _streams = new();

    public AssetId Add(float[] samples, int channels = 2, int rate = Signals.Rate)
    {
        var id = AssetId.New();
        _streams[id] = new MemoryAudioStream(samples, channels, rate);
        return id;
    }

    public IAudioStream? OpenAudio(AssetId asset) => _streams.GetValueOrDefault(asset);
}
