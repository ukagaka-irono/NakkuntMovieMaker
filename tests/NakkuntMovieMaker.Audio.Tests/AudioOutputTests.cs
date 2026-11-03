using System.Diagnostics;

namespace NakkuntMovieMaker.Audio.Tests;

/// <summary>
/// 音の出力。音を出さずに実時間で消費する仮のデバイス（miniaudio の null バックエンド）で確かめる。
/// nmm_audio を用意していない環境ではスキップする（CI では NMM_REQUIRE_NMM_AUDIO=1 で必須にする）
/// </summary>
public class AudioOutputTests
{
    private static void RequireNativeAudio()
    {
        var loaded = NativeAudio.TryGetInfo(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_NMM_AUDIO") != "1", $"nmm_audio を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    [Fact]
    public void ConsumesWrittenAudioInRealTime()
    {
        RequireNativeAudio();
        using var output = AudioOutput.Open(48000, 4800, nullDevice: true);
        var block = new float[480 * 2];

        output.Start();
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 600)
        {
            while (output.WritableFrames >= 480)
            {
                Assert.Equal(480, output.Write(block));
            }

            Thread.Sleep(2);
        }

        var played = output.FramesPlayed;
        var expected = watch.Elapsed.TotalSeconds * 48000;
        output.Stop();

        // 仮のデバイスも実時間で消費する（周期の分のずれを許す）
        Assert.InRange(played, expected - 9600, expected + 4800);
        Assert.True(output.UnderrunFrames < 9600, $"音が足りなかったフレーム: {output.UnderrunFrames}");
    }

    [Fact]
    public void ClearDropsBufferedAudio()
    {
        RequireNativeAudio();
        using var output = AudioOutput.Open(48000, 4800, nullDevice: true);

        Assert.Equal(4800, output.WritableFrames);
        output.Write(new float[2000 * 2]);
        Assert.Equal(2800, output.WritableFrames);
        output.Clear(); // 止まっているので、すぐに捨てる
        Assert.Equal(4800, output.WritableFrames);

        // 鳴らしている途中でも捨てられる
        output.Write(new float[4800 * 2]);
        output.Start();
        output.Clear();
        Assert.True(output.WritableFrames > 4000);
        output.Stop();
    }

    [Fact]
    public void ReportsInvalidUse()
    {
        RequireNativeAudio();
        var output = AudioOutput.Open(48000, 1024, nullDevice: true);
        output.Dispose();

        Assert.Throws<ObjectDisposedException>(() => output.WritableFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutput.Open(0, 1024, nullDevice: true));
    }
}
