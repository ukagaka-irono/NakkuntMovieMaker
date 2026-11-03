using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// 波形を表示するための、一定のサンプル数ごとの最小値と最大値（ピーク）。
/// 値は -1〜1 を 16 ビットの整数にしたもので、最小値は切り下げ・最大値は切り上げて、実際の波形より小さく見えないようにする
/// </summary>
public sealed class AudioPeaks
{
    /// <summary>1 つのピークにまとめるサンプルの数（48kHz で約 5.3 ミリ秒）</summary>
    public const int SamplesPerPeak = 256;

    private const int Version = 1;
    private const int HeaderSize = 32;

    // ピークごと・チャンネルごとに、最小値と最大値の順で並べる
    private readonly short[] _values;

    private AudioPeaks(int sampleRate, int channels, long frameCount, short[] values)
    {
        SampleRate = sampleRate;
        Channels = channels;
        FrameCount = frameCount;
        _values = values;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>元の音声の、1 チャンネルあたりのサンプルの数</summary>
    public long FrameCount { get; }

    /// <summary>ピークの数</summary>
    public int PeakCount => _values.Length / (Channels * 2);

    private static ReadOnlySpan<byte> Magic => "NMMPEAK\0"u8;

    /// <summary>PCM キャッシュからピークを作る</summary>
    public static AudioPeaks Build(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        var peakCount = checked((int)((audio.FrameCount + SamplesPerPeak - 1) / SamplesPerPeak));
        var values = new short[peakCount * audio.Channels * 2];
        for (var peak = 0; peak < peakCount; peak++)
        {
            if (peak % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var start = (long)peak * SamplesPerPeak;
            var samples = audio.GetSamples(start, (int)Math.Min(SamplesPerPeak, audio.FrameCount - start));
            for (var channel = 0; channel < audio.Channels; channel++)
            {
                var min = float.MaxValue;
                var max = float.MinValue;
                for (var i = channel; i < samples.Length; i += audio.Channels)
                {
                    min = Math.Min(min, samples[i]);
                    max = Math.Max(max, samples[i]);
                }

                var offset = (peak * audio.Channels + channel) * 2;
                values[offset] = Quantize(min, Math.Floor);
                values[offset + 1] = Quantize(max, Math.Ceiling);
            }
        }

        return new AudioPeaks(audio.SampleRate, audio.Channels, audio.FrameCount, values);
    }

    /// <summary>
    /// [startFrame, endFrame) の範囲の最小値と最大値（-1〜1）を返す。ピーク単位でまとめるので、
    /// 範囲の端は最大で SamplesPerPeak − 1 サンプル外側まで含む。音声の範囲の外は無音として扱う
    /// </summary>
    public (float Min, float Max) GetRange(int channel, long startFrame, long endFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, Channels);

        var first = Math.Max(0, startFrame) / SamplesPerPeak;
        var last = (Math.Min(endFrame, FrameCount) + SamplesPerPeak - 1) / SamplesPerPeak;
        if (first >= last)
        {
            return (0, 0);
        }

        short min = short.MaxValue;
        short max = short.MinValue;
        for (var peak = (int)first; peak < last; peak++)
        {
            var offset = (peak * Channels + channel) * 2;
            min = Math.Min(min, _values[offset]);
            max = Math.Max(max, _values[offset + 1]);
        }

        // 範囲の一部が音声の外なら、そこは無音（0）なので含める
        if (startFrame < 0 || endFrame > FrameCount)
        {
            min = Math.Min(min, (short)0);
            max = Math.Max(max, (short)0);
        }

        return (min / 32767f, max / 32767f);
    }

    public void Save(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], SamplesPerPeak);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], Channels);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], FrameCount);
        stream.Write(header);
        stream.Write(MemoryMarshal.AsBytes(_values.AsSpan()));
    }

    /// <exception cref="InvalidDataException">ピークのファイルの形式ではない、または途中で切れている</exception>
    public static AudioPeaks Load(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("ピークのファイルが途中で切れています", ex);
        }

        if (!header.StartsWith(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != SamplesPerPeak)
        {
            throw new InvalidDataException("ピークのファイルの形式ではないか、版が違います");
        }

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var channels = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        var frameCount = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        if (sampleRate <= 0 || channels is < 1 or > 2 || frameCount < 0)
        {
            throw new InvalidDataException("ピークのファイルのヘッダーが正しくありません");
        }

        var peakCount = checked((int)((frameCount + SamplesPerPeak - 1) / SamplesPerPeak));
        var values = new short[peakCount * channels * 2];
        try
        {
            stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("ピークのファイルが途中で切れています", ex);
        }

        return new AudioPeaks(sampleRate, channels, frameCount, values);
    }

    private static short Quantize(float value, Func<double, double> round) =>
        (short)Math.Clamp(round(Math.Clamp(value, -1f, 1f) * 32767.0), -32767, 32767);
}
