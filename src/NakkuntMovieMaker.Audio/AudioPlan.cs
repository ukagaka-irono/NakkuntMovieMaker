using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Audio;

/// <summary>
/// シーンの音をどう混ぜるか（映像の FramePlan にあたるもの）。位置と長さは、シーンのサンプルレートでのサンプル数。
/// DuckingTriggers は、BGM を下げるきっかけになる区間（セリフが鳴っている区間）
/// </summary>
public sealed record AudioPlan(
    int SampleRate,
    Rational Fps,
    ImmutableArray<AudioClip> Clips,
    ImmutableArray<SampleRange> DuckingTriggers,
    long Length);

/// <summary>サンプルの範囲 [Start, End)</summary>
public readonly record struct SampleRange(long Start, long End);

/// <summary>
/// 1 つのアイテムの音。Start・Length はタイムラインのサンプル（Start はアイテムの先頭のフレームの最初のサンプル）。
/// 音量・パンのキーフレームは、アイテムの先頭からのフレームで評価する。
/// 素材は [SourceOffset, SourceOffset + 長さ × Speed) 秒の範囲を使い、Reverse なら後ろから使う。
/// フェードはサンプル数で、DuckingDb はセリフの間に下げる量（デシベル）
/// </summary>
public sealed record AudioClip(
    ItemId Item,
    AssetId Asset,
    long Start,
    long Length,
    Rational SourceOffset,
    Rational Speed,
    bool Reverse,
    bool PreservePitch,
    Animatable<double> Volume,
    Animatable<double> Pan,
    long FadeIn,
    long FadeOut,
    double DuckingDb)
{
    public long End => Start + Length;
}

/// <summary>
/// 音声の素材。サンプルはインターリーブの float32 で、範囲の外は無音になる
/// </summary>
public interface IAudioStream
{
    int SampleRate { get; }

    int Channels { get; }

    long FrameCount { get; }

    void Read(long startFrame, Span<float> destination);
}

/// <summary>素材の ID から音声を開く。音声がない・読めない素材は null</summary>
public interface IAudioSource
{
    IAudioStream? OpenAudio(AssetId asset);
}

/// <summary>PCM キャッシュを IAudioStream として読む</summary>
public sealed class PcmAudioStream(PcmAudio audio) : IAudioStream
{
    public int SampleRate => audio.SampleRate;

    public int Channels => audio.Channels;

    public long FrameCount => audio.FrameCount;

    public void Read(long startFrame, Span<float> destination) => audio.Read(startFrame, destination);
}

/// <summary>メモリ上のサンプル（テストや、録音・合成した音声に使う）</summary>
public sealed class MemoryAudioStream(float[] samples, int channels, int sampleRate) : IAudioStream
{
    public int SampleRate => sampleRate;

    public int Channels => channels;

    public long FrameCount => samples.Length / channels;

    public void Read(long startFrame, Span<float> destination)
    {
        destination.Clear();
        var frames = destination.Length / channels;
        var first = Math.Clamp(startFrame, 0, FrameCount);
        var last = Math.Clamp(startFrame + frames, 0, FrameCount);
        if (last > first)
        {
            samples.AsSpan((int)(first * channels), (int)((last - first) * channels))
                .CopyTo(destination[(int)((first - startFrame) * channels)..]);
        }
    }
}
