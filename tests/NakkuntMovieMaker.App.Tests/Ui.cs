using Avalonia;
using Avalonia.Headless;
using NakkuntMovieMaker.App.Services;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>画面のないまま Avalonia を動かす。画面に触れるテストは Run の中で行う（UI のスレッドで実行される）</summary>
public static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(Ui)));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static Task Run(Action action) => Session.Value.Dispatch(action, TestContext.Current.CancellationToken);

    public static Task Run(Func<Task> action) => Session.Value.Dispatch(
        async () =>
        {
            await action();
            return true;
        },
        TestContext.Current.CancellationToken);
}

/// <summary>テスト用の一時フォルダと、音のデバイスを使わない編集の状態</summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Directory.CreateDirectory(Folder);
        Settings = new AppSettings();
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public AppSettings Settings { get; }

    public string SettingsPath => Path.Combine(Folder, "settings.json");

    /// <summary>自動保存の復元の記録（利用者の本物の記録には触れない）</summary>
    public string RecoveryFolder => Path.Combine(Folder, "recovery");

    public EditorSession CreateSession() =>
        new(Settings, disableAudioDevice: true, settingsPath: SettingsPath, voiceEngines: () => new Voice.VoiceEngineRegistry([new TestVoiceEngine()]));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Media のテスト用の素材</summary>
public static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static void RequireFfmpeg()
    {
        var loaded = Media.FfmpegLibrary.TryLoad(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") != "1", $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    /// <summary>素材を一時フォルダに写す（場所を変えるテスト用）</summary>
    public static string CopyTo(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, name);
        File.Copy(Path(name), target, overwrite: true);
        return target;
    }
}

/// <summary>テスト用の音声合成エンジン（ID "test"）。読み（なければ本文）の 1 文字を 0.1 秒の音にする</summary>
public sealed class TestVoiceEngine : Voice.IVoiceEngine
{
    public string Id => "test";

    public string Name => "テスト用";

    public string Version => "1";

    public Voice.VoiceEngineStatus Status => Voice.VoiceEngineStatus.Ready;

    public IReadOnlyList<Voice.VoiceInfo> Voices { get; } = [new("a", "声 A")];

    public string? MakeReading(string text) => text;

    public Voice.VoiceAudio Synthesize(Voice.VoiceRequest request, CancellationToken cancellationToken)
    {
        var length = (request.Reading ?? request.Text).Length * 4800;
        return new Voice.VoiceAudio([.. Enumerable.Range(0, length).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / 48000)))], 48000);
    }

    public void Dispose()
    {
    }
}

/// <summary>テスト用の録音の入力。始めてから経った時間の分だけ、振幅 0.3 の音を返す（本物のマイクは使わない）</summary>
public sealed class FakeAudioInput : Audio.IAudioInput
{
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _read;

    public int SampleRate => 48000;

    public int Channels => 1;

    public int LatencyFrames => 0;

    public int PeriodFrames => 480;

    public long OverrunFrames => 0;

    public bool Disposed { get; private set; }

    public void Start() => _clock.Start();

    public void Stop() => _clock.Stop();

    public int Read(Span<float> destination)
    {
        var available = (long)(_clock.Elapsed.TotalSeconds * SampleRate) - _read;
        var frames = (int)Math.Clamp(available, 0, destination.Length);
        for (var i = 0; i < frames; i++)
        {
            destination[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * (_read + i) / SampleRate));
        }

        _read += frames;
        return frames;
    }

    public void Dispose() => Disposed = true;
}
