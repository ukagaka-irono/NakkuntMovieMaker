using Avalonia.Controls;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views;

public partial class PreviewView : UserControl
{
    private PreviewViewModel? _attached;
    private FullScreenPreviewWindow? _fullScreen;

    public PreviewView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached is not null)
        {
            _attached.FullScreenRequested -= OnFullScreenRequested;
        }

        _attached = DataContext as PreviewViewModel;
        if (_attached is not null)
        {
            _attached.FullScreenRequested += OnFullScreenRequested;
        }
    }

    // 全画面のプレビューを開く（開いていれば閉じる）
    private void OnFullScreenRequested(object? sender, EventArgs e)
    {
        if (_fullScreen is not null)
        {
            _fullScreen.Close();
            return;
        }

        _fullScreen = new FullScreenPreviewWindow { DataContext = _attached };
        _fullScreen.Closed += (_, _) => _fullScreen = null;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _fullScreen.Show(owner);
        }
        else
        {
            _fullScreen.Show();
        }
    }
}
