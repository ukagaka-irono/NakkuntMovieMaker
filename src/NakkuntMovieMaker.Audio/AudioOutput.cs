using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Audio;

/// <summary>オーディオデバイスを使えなかった。Message は利用者に表示できる文</summary>
public sealed class AudioDeviceException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// 音の出力（miniaudio の既定の出力デバイス）。ステレオの float32 を Write でリングバッファに書き込むと、デバイスが順に鳴らす。
/// デバイスのコールバックはネイティブのコードだけで動くので、.NET の処理が少し止まっても音は途切れない（バッファが残っている間）。
/// Write・Clear は 1 つのスレッドから呼ぶこと
/// </summary>
public sealed partial class AudioOutput : IDisposable
{
    public const int Channels = 2;

    private nint _handle;
    private bool _started;

    private AudioOutput(nint handle, int sampleRate, int bufferFrames)
    {
        _handle = handle;
        SampleRate = sampleRate;
        BufferFrames = bufferFrames;
    }

    public int SampleRate { get; }

    /// <summary>リングバッファの大きさ（フレーム）</summary>
    public int BufferFrames { get; }

    /// <summary>リングバッファに書き込めるフレームの数</summary>
    public int WritableFrames => (int)nmm_playback_writable(Handle);

    /// <summary>デバイスに渡したフレームの合計（音が足りずに無音を出したぶんは数えない）</summary>
    public long FramesPlayed => (long)nmm_playback_frames_played(Handle);

    /// <summary>音が足りずに無音を出したフレームの合計</summary>
    public long UnderrunFrames => (long)nmm_playback_underrun_frames(Handle);

    /// <summary>デバイスに渡してから実際に鳴るまでの目安（フレーム）</summary>
    public int LatencyFrames => (int)nmm_playback_latency_frames(Handle);

    public bool IsStarted => _started;

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(AudioOutput));

    /// <summary>
    /// 出力デバイスを開く。deviceName が null なら既定のデバイス（名前は AudioDevices.List で調べる）。
    /// nullDevice なら、音を出さずに実時間で消費するだけの仮のデバイスを使う（テスト用）
    /// </summary>
    /// <exception cref="AudioDeviceException">デバイスを開けなかった（nmm_audio がない場合も含む）</exception>
    public static AudioOutput Open(int sampleRate, int bufferFrames, bool nullDevice = false, string? deviceName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferFrames);
        try
        {
            var result = nmm_playback_open((uint)sampleRate, Channels, (uint)bufferFrames, nullDevice ? 1 : 0, deviceName, out var handle);
            if (result != 0 || handle == 0)
            {
                throw new AudioDeviceException(AudioDevices.DescribeOpenError("出力", deviceName, result));
            }

            return new AudioOutput(handle, sampleRate, bufferFrames);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioDeviceException("nmm_audio を読み込めないため、音を出せません", ex);
        }
    }

    public void Start()
    {
        if (!_started)
        {
            Check(nmm_playback_start(Handle), "再生の開始");
            _started = true;
        }
    }

    public void Stop()
    {
        if (_started)
        {
            Check(nmm_playback_stop(Handle), "再生の停止");
            _started = false;
        }
    }

    /// <summary>ステレオのサンプルを書き込む（待たない）。書き込めたフレームの数を返す</summary>
    public unsafe int Write(ReadOnlySpan<float> stereo)
    {
        fixed (float* frames = stereo)
        {
            return (int)nmm_playback_write(Handle, frames, (uint)(stereo.Length / Channels));
        }
    }

    /// <summary>まだ鳴らしていない音を捨てる（シークのとき）。鳴らしている途中なら、捨て終わるまで待つ</summary>
    public void Clear()
    {
        if (nmm_playback_clear(Handle) != 0)
        {
            return;
        }

        var deadline = Environment.TickCount64 + 500;
        while (nmm_playback_clearing(Handle) != 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            nmm_playback_close(handle);
        }
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new AudioDeviceException($"{what}に失敗しました（miniaudio のエラー {result}）");
        }
    }

    [LibraryImport("nmm_audio", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int nmm_playback_open(uint sampleRate, uint channels, uint bufferFrames, int nullBackend, string? deviceName, out nint playback);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_playback_start(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_playback_stop(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial uint nmm_playback_writable(nint playback);

    [LibraryImport("nmm_audio")]
    private static unsafe partial uint nmm_playback_write(nint playback, float* frames, uint frameCount);

    [LibraryImport("nmm_audio")]
    private static partial ulong nmm_playback_frames_played(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial ulong nmm_playback_underrun_frames(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial uint nmm_playback_latency_frames(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_playback_clear(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_playback_clearing(nint playback);

    [LibraryImport("nmm_audio")]
    private static partial void nmm_playback_close(nint playback);
}
