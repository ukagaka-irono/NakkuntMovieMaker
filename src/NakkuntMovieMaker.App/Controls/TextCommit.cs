using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Controls;

/// <summary>
/// 文字の入力欄で、入力し終えたとき（フォーカスが外れた・1 行の欄で Enter）に TextEditor.Commit を呼ぶ。
/// 1 文字ごとに操作を作らないようにする
/// </summary>
public static class TextCommit
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Enabled", typeof(TextCommit));

    static TextCommit()
    {
        EnabledProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.NewValue is true)
            {
                box.LostFocus += OnLostFocus;
                box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }
            else
            {
                box.LostFocus -= OnLostFocus;
                box.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            }
        });
    }

    public static bool GetEnabled(TextBox box) => box.GetValue(EnabledProperty);

    public static void SetEnabled(TextBox box, bool value) => box.SetValue(EnabledProperty, value);

    private static void OnLostFocus(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as TextEditor)?.Commit();

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { AcceptsReturn: false, DataContext: TextEditor editor })
        {
            editor.Commit();
            e.Handled = true;
        }
    }
}
