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
