using NakkuntMovieMaker.Audio;

namespace NakkuntMovieMaker.App.Services;

/// <summary>
/// セリフの試聴。WAV を 1 つ、タイムラインとは別に鳴らす（鳴らしている途中で次を頼むと、前のものは止める）。
/// 音のデバイスを使わない設定なら鳴らさない
/// </summary>
public sealed class VoicePlayer(bool disabled, Func<string?> outputDevice) : IDisposable
{
    private const int SampleRate = 48000;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    /// <summary>最後に鳴らしたファイル（テスト用）</summary>
    public string? LastPlayed { get; private set; }

    /// <summary>WAV を鳴らし始める（鳴り終わるまで待たない）</summary>
    public void Play(string path)
    {
        LastPlayed = path;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            _current?.Cancel();
            _current = cancellation = new CancellationTokenSource();
        }

        if (disabled)
        {
            return;
        }

        var device = outputDevice();
        _ = Task.Run(() => Run(path, device, cancellation.Token));
    }

    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
        }
    }

    public void Dispose() => Stop();

    private static void Run(string path, string? device, CancellationToken cancellationToken)
    {
        try
        {
            var stereo = ToStereo48k(WavReader.Read(path));
            using var output = AudioOutput.Open(SampleRate, SampleRate / 5, deviceName: device);
            var written = 0;
            output.Start();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (written < stereo.Length / 2)
                {
                    var frames = Math.Min(output.WritableFrames, stereo.Length / 2 - written);
                    written += output.Write(stereo.AsSpan(written * 2, frames * 2));
                }
                else if (output.FramesPlayed >= written)
                {
                    break;
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is AudioDeviceException or IOException or InvalidDataException or DllNotFoundException)
        {
            // 鳴らせなくても編集は続けられるので、知らせない
        }
    }

    // 声のキャッシュは 48kHz。ほかの形式なら近い方法で 48kHz・ステレオにする
    private static float[] ToStereo48k(WavData wav)
    {
        var frames = wav.FrameCount;
        var ratio = (double)wav.SampleRate / SampleRate;
        var outputFrames = (int)(frames / ratio);
        var stereo = new float[outputFrames * 2];
        for (var i = 0; i < outputFrames; i++)
        {
            var source = Math.Min(frames - 1, (long)(i * ratio));
            var left = wav.Samples[source * wav.Channels];
            var right = wav.Channels > 1 ? wav.Samples[source * wav.Channels + 1] : left;
            stereo[i * 2] = left;
            stereo[i * 2 + 1] = right;
        }

        return stereo;
    }
}
