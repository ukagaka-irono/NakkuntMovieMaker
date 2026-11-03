using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views;

public partial class PropertiesView : UserControl
{
    public PropertiesView()
    {
        InitializeComponent();
    }

    // 文字は入力し終えたとき（フォーカスが外れた・1 行の欄で Enter）に反映する
    private void OnTextLostFocus(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as TextEditor)?.Commit();

    private void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (sender as Control)?.DataContext is TextEditor { Multiline: false } editor)
        {
            editor.Commit();
            e.Handled = true;
        }
    }
}
