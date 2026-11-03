using Avalonia.Controls;
using Avalonia.Interactivity;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views.Dialogs;

public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
    }

    // 書き出している間は閉じない（中止してから閉じる）
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is ExportViewModel { IsExporting: true })
        {
            e.Cancel = true;
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExportViewModel { Result: { } path } && Path.GetDirectoryName(path) is { } folder)
        {
            await Launcher.LaunchUriAsync(new Uri(folder));
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
