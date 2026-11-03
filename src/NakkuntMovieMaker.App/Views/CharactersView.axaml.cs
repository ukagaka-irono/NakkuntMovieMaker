using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.App.Views.Dialogs;

namespace NakkuntMovieMaker.App.Views;

public partial class CharactersView : UserControl
{
    private CharactersViewModel? _attached;

    public CharactersView()
    {
        InitializeComponent();
        NameBox.LostFocus += (_, _) => ViewModel?.CommitName();
        NameBox.AddHandler(KeyDownEvent, OnNameKeyDown, RoutingStrategies.Tunnel);
    }

    private CharactersViewModel? ViewModel => DataContext as CharactersViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached is not null)
        {
            _attached.EngineGuideRequested -= OnEngineGuideRequested;
        }

        _attached = ViewModel;
        if (_attached is not null)
        {
            _attached.EngineGuideRequested += OnEngineGuideRequested;
        }
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.CommitName();
            e.Handled = true;
        }
    }

    // 音声合成エンジンと AquesTalk の導入の案内
    private async void OnEngineGuideRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && owner.DataContext is MainWindowViewModel main)
        {
            await new VoiceEnginesDialog { DataContext = new VoiceEnginesViewModel(main.Session, main.Dialogs) }.ShowDialog(owner);
        }
    }
}
