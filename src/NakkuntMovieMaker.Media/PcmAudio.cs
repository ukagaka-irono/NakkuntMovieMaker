using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// PCM キャッシュのファイル（float32・インターリーブ）を、メモリマップで読む。
/// 音声のミックスは、デコードせずにここから必要な範囲を直接読む。
/// 読み出しは複数のスレッドから同時に行ってよい
/// </summary>
public sealed unsafe class PcmAudio : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _base;
    private float* _samples;

    private PcmAudio(MemoryMappedFile file, MemoryMappedViewAccessor view, int sampleRate, int channels, long frameCount)
    {
        _file = file;
        _view = view;
        SampleRate = sampleRate;
        Channels = channels;
        FrameCount = frameCount;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        _samples = (float*)(_base + _view.PointerOffset + PcmFile.HeaderSize);
    }

    /// <summary>サンプルレート（Hz）</summary>
    public int SampleRate { get; }

    /// <summary>チャンネルの数（1 か 2）</summary>
    public int Channels { get; }

    /// <summary>1 チャンネルあたりのサンプルの数</summary>
    public long FrameCount { get; }

    /// <summary>長さ（秒）</summary>
    public Rational Duration => new(FrameCount, SampleRate);

    /// <exception cref="InvalidDataException">PCM キャッシュの形式ではない、または途中で切れている</exception>
    public static PcmAudio Open(string path)
    {
        // キャッシュを消せるように、開いている間も削除を許可する
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            Span<byte> header = stackalloc byte[PcmFile.HeaderSize];
            if (stream.Length < PcmFile.HeaderSize)
            {
                throw new InvalidDataException($"PCM キャッシュが途中で切れています: {path}");
            }

            stream.ReadExactly(header);
            var (sampleRate, channels, frameCount) = PcmFile.ReadHeader(header, path);
            if (stream.Length != PcmFile.HeaderSize + frameCount * channels * sizeof(float))
            {
                throw new InvalidDataException($"PCM キャッシュの大きさが合いません: {path}");
            }

            var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            try
            {
                var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return new PcmAudio(file, view, sampleRate, channels, frameCount);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// startFrame から destination の長さぶん（チャンネルの数で割った数のフレーム）を、インターリーブのまま写す。
    /// 素材の範囲の外（負の位置や最後より後）は無音にする
    /// </summary>
    public void Read(long startFrame, Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_samples is null, this);
        if (destination.Length % Channels != 0)
        {
            throw new ArgumentException("長さがチャンネルの数で割り切れません", nameof(destination));
        }

        var frames = destination.Length / Channels;
        var first = Math.Clamp(startFrame, 0, FrameCount);
        var last = Math.Clamp(startFrame + frames, 0, FrameCount);
        var before = (int)(first - startFrame);
        destination[..(before * Channels)].Clear();
        if (last > first)
        {
            var count = (int)(last - first);
            new ReadOnlySpan<float>(_samples + first * Channels, count * Channels).CopyTo(destination[(before * Channels)..]);
            before += count;
        }

        destination[(before * Channels)..].Clear();
    }

    /// <summary>素材の範囲内のサンプルを、写さずにそのまま参照する。PcmAudio を破棄した後は使えない</summary>
    public ReadOnlySpan<float> GetSamples(long startFrame, int frameCount)
    {
        ObjectDisposedException.ThrowIf(_samples is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startFrame + frameCount, FrameCount, nameof(frameCount));
        return new ReadOnlySpan<float>(_samples + startFrame * Channels, frameCount * Channels);
    }

    public void Dispose()
    {
        if (_samples is null)
        {
            return;
        }

        _samples = null;
        _base = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>
/// PCM キャッシュのファイルの形式。64 バイトのヘッダーの後に float32 のサンプルがインターリーブで並ぶ（リトルエンディアン）。
/// ヘッダー：識別子 "NMMPCM" と 2 バイトの 0、版（int32）、サンプルレート（int32）、チャンネルの数（int32）、
/// 予備（int32）、フレームの数（int64）、残りは 0
/// </summary>
internal static class PcmFile
{
    public const int HeaderSize = 64;
    public const int Version = 1;

    private static ReadOnlySpan<byte> Magic => "NMMPCM\0\0"u8;

    public static void WriteHeader(Span<byte> header, int sampleRate, int channels, long frameCount)
    {
        header[..HeaderSize].Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], channels);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], frameCount);
    }

    public static (int SampleRate, int Channels, long FrameCount) ReadHeader(ReadOnlySpan<byte> header, string path)
    {
        if (!header.StartsWith(Magic) || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Version)
        {
            throw new InvalidDataException($"PCM キャッシュの形式ではないか、版が違います: {path}");
        }

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var channels = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var frameCount = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        if (sampleRate <= 0 || channels is < 1 or > 2 || frameCount < 0)
        {
            throw new InvalidDataException($"PCM キャッシュのヘッダーが正しくありません: {path}");
        }

        return (sampleRate, channels, frameCount);
    }
}

/// <summary>PCM キャッシュのファイルを書く。最後に Complete を呼ぶと、ヘッダーにフレームの数が入る</summary>
internal sealed class PcmFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly float[] _silence = new float[4096];

    public PcmFileWriter(string path, int sampleRate, int channels)
    {
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        _sampleRate = sampleRate;
        _channels = channels;
        Span<byte> header = stackalloc byte[PcmFile.HeaderSize];
        PcmFile.WriteHeader(header, sampleRate, channels, 0);
        _stream.Write(header);
    }

    /// <summary>書いたフレームの数</summary>
    public long FrameCount { get; private set; }

    /// <summary>インターリーブのサンプルを書く</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        _stream.Write(MemoryMarshal.AsBytes(samples));
        FrameCount += samples.Length / _channels;
    }

    /// <summary>無音を書く</summary>
    public void WriteSilence(long frames)
    {
        var remaining = frames * _channels;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, _silence.Length);
            _stream.Write(MemoryMarshal.AsBytes(_silence.AsSpan(0, count)));
            remaining -= count;
        }

        FrameCount += Math.Max(0, frames);
    }

    public void Complete()
    {
        Span<byte> header = stackalloc byte[PcmFile.HeaderSize];
        PcmFile.WriteHeader(header, _sampleRate, _channels, FrameCount);
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose() => _stream.Dispose();
}
