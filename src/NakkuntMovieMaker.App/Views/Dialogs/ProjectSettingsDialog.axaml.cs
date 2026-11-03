using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NakkuntMovieMaker.App.Views.Dialogs;

public partial class ProjectSettingsDialog : Window
{
    public ProjectSettingsDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
