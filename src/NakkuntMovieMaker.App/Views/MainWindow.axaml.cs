using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } viewModel)
        {
            viewModel.Shortcuts.Changed += (_, _) => ShowGestures();
            ShowGestures();
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel is not { } viewModel)
        {
            return;
        }

        // 保存の確認が終わるまで閉じない
        e.Cancel = true;
        if (await viewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel?.Dispose();
    }

    // ショートカットはウィンドウ全体で先に受け取る（文字の入力中は、入力欄に任せるキーを除く）
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Handled)
        {
            return;
        }

        var textFocused = FocusManager?.GetFocusedElement() is TextBox;
        if (viewModel.Shortcuts.TryHandle(e.Key, e.KeyModifiers, textFocused))
        {
            e.Handled = true;
        }
    }

    // メニューに、割り当てたショートカットのキーを表示する
    private void ShowGestures()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        foreach (var item in MainMenu.GetLogicalDescendants().OfType<MenuItem>())
        {
            if (item.Tag is string id && viewModel.Shortcuts.Commands.Any(command => command.Id == id))
            {
                item.InputGesture = viewModel.Shortcuts.GestureOf(id);
            }
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
