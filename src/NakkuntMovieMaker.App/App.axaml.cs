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
            var session = new EditorSession(settings);
            window.DataContext = new MainWindowViewModel(session, shortcuts, new WindowDialogService(window));

            // 起動時の引数にプロジェクトがあれば開く
            if (desktop.Args is [var path, ..] && File.Exists(path))
            {
                try
                {
                    session.Open(path);
                }
                catch (Exception ex) when (ex is Core.Serialization.ProjectFormatException or IOException or UnauthorizedAccessException)
                {
                }
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
