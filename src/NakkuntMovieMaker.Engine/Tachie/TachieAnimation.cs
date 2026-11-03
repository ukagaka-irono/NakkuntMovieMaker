using System.Collections.Concurrent;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.Engine.Tachie;

/// <summary>セリフの声の、フレームごとの大きさ（口パク用。セリフの先頭からのフレーム、0〜1）</summary>
public interface ILipSyncSource
{
    /// <summary>声がまだない・読めないときは null</summary>
    float[]? Envelope(VoiceItem voice, Rational fps);
}

/// <summary>
/// 目パチと口パクの動き。目パチの間隔は、立ち絵のアイテムごとのシード（アイテムの ID から決める）による乱数なので、
/// 何度描いても同じフレームで同じように瞬きする
/// </summary>
public static class TachieAnimation
{
    // 瞬きの間隔（秒）の範囲
    private const double MinBlinkInterval = 2.5;
    private const double MaxBlinkInterval = 5.5;

    /// <summary>口の大きさ（0〜1）を、口の画像の番号（0 が閉じた状態、frameCount − 1 がいちばん開いた状態）にする</summary>
    public static int MouthFrame(int frameCount, float level) =>
        frameCount <= 1 ? 0 : (int)Math.Clamp(Math.Round(level * (frameCount - 1)), 0, frameCount - 1);

    /// <summary>
    /// アイテムの先頭から local フレーム目の、目の画像の番号（frameCount − 1 が開いた状態）。
    /// 瞬きは、開いた状態から閉じた状態まで 1 枚ずつ進み、また開く（1 枚あたり 1/30 秒）
    /// </summary>
    public static int EyeFrame(int frameCount, long local, Rational fps, int seed)
    {
        var open = frameCount - 1;
        if (frameCount <= 1 || local < 0)
        {
            return Math.Max(0, open);
        }

        var rate = fps.ToDouble();
        var step = Math.Max(1, (long)Math.Round(rate / 30));

        // 開く → 閉じる → 開く の並び（例：4 枚なら 2, 1, 0, 1, 2）
        var sequence = Enumerable.Range(1, open).Select(k => open - k).Concat(Enumerable.Range(1, open - 1)).ToArray();
        var blinkLength = sequence.Length * step;

        var start = 0L;
        for (var blink = 0; ; blink++)
        {
            start += (long)Math.Round((MinBlinkInterval + Random(seed, blink) * (MaxBlinkInterval - MinBlinkInterval)) * rate);
            if (local < start)
            {
                return open;
            }

            if (local < start + blinkLength)
            {
                return sequence[(local - start) / step];
            }
        }
    }

    /// <summary>アイテムの ID から、目パチのシードを決める</summary>
    public static int Seed(ItemId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.Value.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes) ^ BitConverter.ToInt32(bytes[4..]) ^ BitConverter.ToInt32(bytes[8..]) ^ BitConverter.ToInt32(bytes[12..]);
    }

    // 0 以上 1 未満の、シードと番号から決まる乱数（どの環境でも同じ値）
    private static double Random(int seed, int index)
    {
        var x = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)index * 0xC2B2AE3D27D4EB4FUL);
        x ^= x >> 30;
        x = unchecked(x * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 27;
        x = unchecked(x * 0x94D049BB133111EBUL);
        x ^= x >> 31;
        return (x >> 11) * (1.0 / (1UL << 53));
    }
}

/// <summary>
/// プロジェクトのセリフの声から、口パク用の大きさを求める（合成した声はキャッシュ、録音した声は素材から読む）。
/// 求めたものは、声とフレームレートごとに覚えておく
/// </summary>
public sealed class ProjectLipSync(IAudioSource audio) : ILipSyncSource
{
    private readonly ConcurrentDictionary<(string Voice, Rational Fps), float[]?> _envelopes = new();

    public float[]? Envelope(VoiceItem voice, Rational fps)
    {
        var key = voice.Source switch
        {
            SynthesizedVoice { CacheKey: { } cacheKey } => "voice:" + cacheKey,
            RecordedVoice recorded => "asset:" + recorded.Asset,
            _ => null,
        };
        if (key is null)
        {
            return null;
        }

        return _envelopes.GetOrAdd((key, fps), entry =>
        {
            var stream = voice.Source is RecordedVoice recorded ? audio.OpenAudio(recorded.Asset) : audio.OpenVoice(entry.Voice["voice:".Length..]);
            if (stream is null)
            {
                return null;
            }

            var frames = (int)Math.Min(stream.FrameCount, int.MaxValue / Math.Max(1, stream.Channels));
            var samples = new float[frames * stream.Channels];
            stream.Read(0, samples);
            var mono = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                for (var channel = 0; channel < stream.Channels; channel++)
                {
                    mono[i] += samples[i * stream.Channels + channel] / stream.Channels;
                }
            }

            return LipSync.Envelope(mono, stream.SampleRate, entry.Fps);
        });
    }
}
