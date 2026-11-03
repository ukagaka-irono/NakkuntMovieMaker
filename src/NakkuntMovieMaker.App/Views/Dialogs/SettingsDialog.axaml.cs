using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views.Dialogs;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    // キーの割り当てを待っているときは、押したキーをそのまま割り当てる（ほかの操作に使わない）
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && viewModel.OnKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
