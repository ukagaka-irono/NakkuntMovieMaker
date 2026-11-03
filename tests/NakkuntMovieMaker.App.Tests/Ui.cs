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

    public EditorSession CreateSession() => new(Settings, disableAudioDevice: true, settingsPath: SettingsPath);

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
