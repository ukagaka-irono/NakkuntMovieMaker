namespace NakkuntMovieMaker.Audio.Tests;

/// <summary>
/// 入力デバイスとデバイスの一覧。仮のデバイス（miniaudio の null バックエンド）で確かめる
/// </summary>
public sealed class AudioInputTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public AudioInputTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static void RequireNativeAudio()
    {
        var loaded = NativeAudio.TryGetInfo(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_NMM_AUDIO") != "1", $"nmm_audio を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    [Fact]
    public void ListsDevices()
    {
        RequireNativeAudio();

        var outputs = AudioDevices.List(capture: false, nullDevice: true);
        var inputs = AudioDevices.List(capture: true, nullDevice: true);

        Assert.NotEmpty(outputs);
        Assert.NotEmpty(inputs);
        Assert.All(outputs.Concat(inputs), device => Assert.False(string.IsNullOrEmpty(device.Name)));
    }

    [Fact]
    public void OpensDevicesByName()
    {
        RequireNativeAudio();
        var name = AudioDevices.List(capture: true, nullDevice: true)[0].Name;

        using var input = AudioInput.Open(48000, 1, 4800, nullDevice: true, deviceName: name);
        var missing = Assert.Throws<AudioDeviceException>(() => AudioInput.Open(48000, 1, 4800, nullDevice: true, deviceName: "存在しないマイク"));
        Assert.Contains("存在しないマイク", missing.Message);
        Assert.Throws<AudioDeviceException>(() => AudioOutput.Open(48000, 4800, nullDevice: true, deviceName: "存在しないスピーカー"));
    }

    [Fact]
    public void RecordsFromTheDeviceInRealTime()
    {
        RequireNativeAudio();
        using var input = AudioInput.Open(48000, 1, 48000, nullDevice: true);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var recorder = Recorder.Start(input, Path.Combine(_folder, "null.wav"), 0);
        Thread.Sleep(600);
        var result = recorder.Stop();
        var expected = watch.Elapsed.TotalSeconds * 48000;

        // 仮のデバイスは実時間で無音を返す（始まるまでの遅れと、周期の分のずれを許す）
        Assert.InRange(result.Frames, expected - 9600, expected + 2400);
        Assert.Equal(0, result.OverrunFrames);
        Assert.Equal(AudioLevel.Silent, recorder.Level);
    }
}
