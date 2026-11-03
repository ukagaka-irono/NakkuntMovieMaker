using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.App.Views;

namespace NakkuntMovieMaker.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var shortcuts = new ShortcutRegistry();
            shortcuts.Load(settings.Shortcuts);
            var window = new MainWindow();
            // 起動時の引数にプロジェクトがあれば、それを開く
            var path = desktop.Args is [var argument, ..] && File.Exists(argument) ? argument : null;
            var session = new EditorSession(settings, openPath: path);
            window.DataContext = new MainWindowViewModel(session, shortcuts, new WindowDialogService(window));

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
