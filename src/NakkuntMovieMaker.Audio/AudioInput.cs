using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Audio;

/// <summary>オーディオデバイス。Name は開くときに指定する名前、IsDefault は OS の既定のデバイスか</summary>
public sealed record AudioDevice(string Name, bool IsDefault);

/// <summary>出力・入力のデバイスの一覧</summary>
public static partial class AudioDevices
{
    /// <summary>デバイスの一覧。capture なら入力（マイクなど）、そうでなければ出力。nullDevice なら仮のデバイスの一覧（テスト用）</summary>
    /// <exception cref="AudioDeviceException">一覧を取得できなかった</exception>
    public static IReadOnlyList<AudioDevice> List(bool capture, bool nullDevice = false)
    {
        try
        {
            var result = nmm_devices_open(nullDevice ? 1 : 0, out var list);
            if (result != 0)
            {
                throw new AudioDeviceException($"オーディオデバイスの一覧を取得できませんでした（miniaudio のエラー {result}）");
            }

            try
            {
                var kind = capture ? 1 : 0;
                return [.. Enumerable.Range(0, (int)nmm_devices_count(list, kind)).Select(index => new AudioDevice(
                    Marshal.PtrToStringUTF8(nmm_devices_name(list, kind, (uint)index)) ?? "",
                    nmm_devices_is_default(list, kind, (uint)index) != 0))];
            }
            finally
            {
                nmm_devices_close(list);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioDeviceException("nmm_audio を読み込めないため、オーディオデバイスを使えません", ex);
        }
    }

    // miniaudio の MA_NO_DEVICE
    private const int NoDevice = -204;

    internal static string DescribeOpenError(string kind, string? deviceName, int result) =>
        result == NoDevice && deviceName is not null
            ? $"音の{kind}デバイス「{deviceName}」が見つかりません"
            : $"音の{kind}デバイスを開けませんでした（miniaudio のエラー {result}）";

    [LibraryImport("nmm_audio")]
    private static partial int nmm_devices_open(int nullBackend, out nint list);

    [LibraryImport("nmm_audio")]
    private static partial uint nmm_devices_count(nint list, int capture);

    [LibraryImport("nmm_audio")]
    private static partial nint nmm_devices_name(nint list, int capture, uint index);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_devices_is_default(nint list, int capture, uint index);

    [LibraryImport("nmm_audio")]
    private static partial void nmm_devices_close(nint list);
}

/// <summary>
/// 録音の入力。Read は録れた分だけを待たずに返す。LatencyFrames は、音が入ってから読めるようになるまでの遅れ、
/// PeriodFrames は 1 回にまとめて届くフレームの数の目安
/// </summary>
public interface IAudioInput : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    int LatencyFrames { get; }

    int PeriodFrames { get; }

    /// <summary>入力が追いつかず捨てたフレームの合計</summary>
    long OverrunFrames { get; }

    void Start();

    void Stop();

    /// <summary>録れた分を destination（インターリーブ）に読み出し、読み出したフレームの数を返す</summary>
    int Read(Span<float> destination);
}

/// <summary>miniaudio の入力デバイス（マイクなど）</summary>
public sealed partial class AudioInput : IAudioInput
{
    private nint _handle;

    private AudioInput(nint handle, int sampleRate, int channels)
    {
        _handle = handle;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int LatencyFrames => (int)nmm_capture_latency_frames(Handle);

    public int PeriodFrames => (int)nmm_capture_period_frames(Handle);

    public long OverrunFrames => (long)nmm_capture_overrun_frames(Handle);

    /// <summary>入力されたフレームの合計</summary>
    public long FramesCaptured => (long)nmm_capture_frames_captured(Handle);

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(AudioInput));

    /// <summary>
    /// 入力デバイスを開く。deviceName が null なら既定のデバイス。bufferFrames は、読み出すまでためておけるフレームの数。
    /// nullDevice なら、実時間で無音を返す仮のデバイスを使う（テスト用）
    /// </summary>
    /// <exception cref="AudioDeviceException">デバイスを開けなかった</exception>
    public static AudioInput Open(int sampleRate, int channels, int bufferFrames, bool nullDevice = false, string? deviceName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferFrames);
        try
        {
            var result = nmm_capture_open((uint)sampleRate, (uint)channels, (uint)bufferFrames, nullDevice ? 1 : 0, deviceName, out var handle);
            if (result != 0 || handle == 0)
            {
                throw new AudioDeviceException(AudioDevices.DescribeOpenError("入力", deviceName, result));
            }

            return new AudioInput(handle, sampleRate, channels);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioDeviceException("nmm_audio を読み込めないため、録音できません", ex);
        }
    }

    public void Start()
    {
        var result = nmm_capture_start(Handle);
        if (result != 0)
        {
            throw new AudioDeviceException($"録音の開始に失敗しました（miniaudio のエラー {result}）");
        }
    }

    public void Stop() => nmm_capture_stop(Handle);

    public unsafe int Read(Span<float> destination)
    {
        fixed (float* frames = destination)
        {
            return (int)nmm_capture_read(Handle, frames, (uint)(destination.Length / Channels));
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            nmm_capture_close(handle);
        }
    }

    [LibraryImport("nmm_audio", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int nmm_capture_open(uint sampleRate, uint channels, uint bufferFrames, int nullBackend, string? deviceName, out nint capture);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_capture_start(nint capture);

    [LibraryImport("nmm_audio")]
    private static partial int nmm_capture_stop(nint capture);

    [LibraryImport("nmm_audio")]
    private static unsafe partial uint nmm_capture_read(nint capture, float* frames, uint frameCount);

    [LibraryImport("nmm_audio")]
    private static partial ulong nmm_capture_frames_captured(nint capture);

    [LibraryImport("nmm_audio")]
    private static partial ulong nmm_capture_overrun_frames(nint capture);

    [LibraryImport("nmm_audio")]
    private static partial uint nmm_capture_latency_frames(nint capture);

    [LibraryImport("nmm_audio")]
    private static partial uint nmm_capture_period_frames(nint capture);

    [LibraryImport("nmm_audio")]
    private static partial void nmm_capture_close(nint capture);
}
