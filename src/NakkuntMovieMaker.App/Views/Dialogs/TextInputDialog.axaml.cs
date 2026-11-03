using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NakkuntMovieMaker.App.Views.Dialogs;

/// <summary>1 行の文字を入れてもらうダイアログ。OK なら入れた文字、キャンセルなら null を返す</summary>
public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
    }

    public TextInputDialog(string title, string label, string initial)
        : this()
    {
        Title = title;
        LabelText.Text = label;
        Input.Text = initial;
        Opened += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Input.Text?.Trim());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
