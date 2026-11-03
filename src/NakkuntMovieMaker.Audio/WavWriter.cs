using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Audio;

/// <summary>WAV のサンプルの形式</summary>
public enum WavSampleFormat
{
    /// <summary>16 ビット整数（多くのソフトで読める。範囲を超えた値は切り詰める）</summary>
    Pcm16,

    /// <summary>32 ビット浮動小数点（劣化しない）</summary>
    Float32,
}

/// <summary>
/// WAV ファイルを書く。サンプルはインターリーブの float32 で渡し、続けて Write で足していける。
/// 最後に破棄すると、ヘッダーに長さを書き込む（録音のように長さが分からないときにも使える）
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderSize = 44;

    private readonly FileStream _stream;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly WavSampleFormat _format;
    private byte[] _buffer = [];
    private long _dataBytes;

    public WavWriter(string path, int channels, int sampleRate, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _channels = channels;
        _sampleRate = sampleRate;
        _format = format;
        _stream.Write(new byte[HeaderSize]);
    }

    private int BytesPerSample => _format == WavSampleFormat.Pcm16 ? 2 : 4;

    /// <summary>書いたフレーム（1 チャンネルあたりのサンプル）の数</summary>
    public long FrameCount => _dataBytes / (BytesPerSample * _channels);

    /// <summary>ステレオなら左右交互に並んだサンプルを書く（-1〜1）</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (_format == WavSampleFormat.Float32)
        {
            _stream.Write(MemoryMarshal.AsBytes(samples));
        }
        else
        {
            if (_buffer.Length < samples.Length * 2)
            {
                _buffer = new byte[samples.Length * 2];
            }

            for (var i = 0; i < samples.Length; i++)
            {
                var value = (short)Math.Clamp(Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(i * 2), value);
            }

            _stream.Write(_buffer, 0, samples.Length * 2);
        }

        _dataBytes += samples.Length * BytesPerSample;
    }

    /// <summary>ステレオなどのサンプルを、1 つの WAV ファイルとして書く</summary>
    public static void WriteFile(string path, ReadOnlySpan<float> samples, int channels, int sampleRate, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        using var writer = new WavWriter(path, channels, sampleRate, format);
        writer.Write(samples);
    }

    public void Dispose()
    {
        if (!_stream.CanWrite)
        {
            return;
        }

        var dataBytes = (uint)Math.Min(_dataBytes, uint.MaxValue - HeaderSize);
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], dataBytes + HeaderSize - 8);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], (short)(_format == WavSampleFormat.Float32 ? 3 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)_channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], _sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], _sampleRate * _channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(_channels * BytesPerSample));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], (short)(BytesPerSample * 8));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataBytes);
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Dispose();
    }
}

/// <summary>オフラインでの書き出し用に、AudioPlan の範囲をまとめて混ぜる</summary>
public static class AudioMixdown
{
    private const int BlockFrames = 4096;

    /// <summary>[start, end)（タイムラインのサンプル）を混ぜたステレオのサンプル</summary>
    public static float[] Render(AudioPlan plan, IAudioSource source, long start, long end, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        var output = new float[checked((int)((end - start) * 2))];
        var mixer = new AudioMixer(plan, source);
        mixer.Seek(start);
        for (var offset = 0; offset < output.Length; offset += BlockFrames * 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mixer.Render(output.AsSpan(offset, Math.Min(BlockFrames * 2, output.Length - offset)));
        }

        return output;
    }
}
