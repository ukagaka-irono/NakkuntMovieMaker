using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Views;

public partial class MediaPoolView : UserControl
{
    private PointerPressedEventArgs? _pressed;
    private Point _dragStart;
    private AssetEntry? _dragEntry;

    public MediaPoolView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AssetList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AssetList.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AssetList.DoubleTapped += (_, _) => ViewModel?.AddToTimelineCommand.Execute(null);
    }

    private MediaPoolViewModel? ViewModel => DataContext as MediaPoolViewModel;

    // OS からファイルをドロップすると素材に加える
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.DataTransfer.TryGetFiles() is not { } files)
        {
            return;
        }

        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToList();
        if (paths.Count > 0)
        {
            await viewModel.ImportAsync(paths);
        }
    }

    // 素材をタイムラインへドラッグする（少し動かしてからドラッグを始める）
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragEntry = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as AssetEntry;
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e : null;
        _dragStart = e.GetPosition(this);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _dragEntry is not { } entry)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) + Math.Abs(position.Y - _dragStart.Y) < 8)
        {
            return;
        }

        _pressed = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(MediaItems.AssetFormat, entry.Id.ToString()));
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy);
    }
}
