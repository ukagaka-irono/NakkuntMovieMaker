using Avalonia;

namespace NakkuntMovieMaker.App;

internal sealed class Program
{
    // AppMain が呼ばれるまでは、Avalonia やサードパーティの API、SynchronizationContext に
    // 依存するコードを使わないこと（まだ初期化されていない）
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia の設定。IDE のプレビューからも使われるので消さないこと
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
