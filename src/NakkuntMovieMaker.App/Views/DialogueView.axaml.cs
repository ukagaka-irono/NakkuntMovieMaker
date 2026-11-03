using Avalonia.Controls;
using Avalonia.Input;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views;

public partial class DialogueView : UserControl
{
    private DialogueViewModel? _attached;

    public DialogueView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached is not null)
        {
            _attached.FocusRequested -= OnFocusRequested;
        }

        _attached = DataContext as DialogueViewModel;
        if (_attached is not null)
        {
            _attached.FocusRequested += OnFocusRequested;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Input.Focus();

    // Enter でセリフを置く（Shift+Enter は何もしない）
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _attached?.AddCommand.CanExecute(null) == true)
        {
            _attached.AddCommand.Execute(null);
            Input.Focus();
            e.Handled = true;
        }
    }
}
