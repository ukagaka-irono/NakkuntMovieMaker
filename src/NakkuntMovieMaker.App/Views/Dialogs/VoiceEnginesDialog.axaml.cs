using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NakkuntMovieMaker.App.Views.Dialogs;

public partial class VoiceEnginesDialog : Window
{
    public VoiceEnginesDialog()
    {
        InitializeComponent();
    }

    // 案内のページは、利用者の既定のブラウザで開く
    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
