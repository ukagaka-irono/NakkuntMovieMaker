using Avalonia.Controls;

namespace NakkuntMovieMaker.App.Views.Dialogs;

/// <summary>メッセージとボタンだけのダイアログ。押したボタンの番号を返す（閉じたら -1）</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <param name="buttons">ボタンの文字。最初のボタンを既定（Enter）、最後をキャンセル（Esc）にする</param>
    public MessageDialog(string title, string message, IReadOnlyList<string> buttons)
        : this()
    {
        Title = title;
        MessageText.Text = message;
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], IsDefault = i == 0, IsCancel = i == buttons.Count - 1 };
            if (i == 0)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) => Close(index);
            Buttons.Children.Add(button);
        }
    }
}
