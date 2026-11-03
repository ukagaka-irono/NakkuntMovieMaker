using Avalonia.Controls;
using Avalonia.Input;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views;

public partial class FullScreenPreviewWindow : Window
{
    public FullScreenPreviewWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape or Key.F11:
                Close();
                e.Handled = true;
                break;
            case Key.Space when DataContext is PreviewViewModel viewModel:
                viewModel.TogglePlayCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
