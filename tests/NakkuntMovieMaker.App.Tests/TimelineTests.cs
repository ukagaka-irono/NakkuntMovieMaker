using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using NakkuntMovieMaker.App.Controls;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>タイムライン：座標・スナップ・編集のコマンド・レイヤー・マーカー・範囲、マウスでの移動とトリム</summary>
public sealed class TimelineTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private static ShapeItem Shape(EditorSession session, int layer, long start, long length)
    {
        var shape = new ShapeItem(ItemId.New(), session.Scene.Layers[layer].Id, start, length, new ShapeStyle(ShapeKind.Rectangle, 10, 10, new SolidPaint(RgbaColor.White)));
        Assert.Null(session.Execute(EditCommands.AddItem(session.Project, session.SceneId, shape)));
        return shape;
    }

    private static Item Get(EditorSession session, ItemId id) => session.Scene.GetItem(id);

    [Fact]
    public Task ConvertsBetweenFramesAndPixels() => Ui.Run(() =>
    {
        using var session = _environment.CreateSession();
        var timeline = new TimelineViewModel(session, _dialogs) { PixelsPerFrame = 2 };
        Shape(session, 0, 0, 3000);
        timeline.ScrollX = 100;

        Assert.Equal(TimelineViewModel.HeaderWidth - 100 + 20, timeline.FrameToX(10L));
        Assert.Equal(10, timeline.XToFrame(timeline.FrameToX(10L) + 0.9));
        Assert.Equal(0, timeline.XToFrame(0));
        Assert.Equal(1, timeline.YToLayer(TimelineViewModel.RulerHeight + TimelineViewModel.LayerHeight * 1.5));
        Assert.Equal(-1, timeline.YToLayer(0));

        // 位置を中心にズームしても、その位置のフレームは動かない
        var x = timeline.FrameToX(500L);
        timeline.ZoomAt(2, x);
        Assert.Equal(4, timeline.PixelsPerFrame);
        Assert.Equal(500, timeline.XToFrame(x));
    });

    [Fact]
    public Task SnapsToEdgesMarkersAndThePlayhead() => Ui.Run(() =>
    {
        using var session = _environment.CreateSession();
        var timeline = new TimelineViewModel(session, _dialogs) { PixelsPerFrame = 2, Snap = true };
        var shape = Shape(session, 0, 30, 30);
        session.CurrentFrame = 100;
        session.Execute(new SetMarker(session.SceneId, MarkerId.New(), new Marker(MarkerId.New(), 200, "a", RgbaColor.White)));

        Assert.Equal(60, timeline.SnapFrame(57)); // 4 ピクセル先にアイテムの終わり
        Assert.Equal(60L, timeline.SnapIndicator);
        Assert.Equal(100, timeline.SnapFrame(103));
        Assert.Equal(200, timeline.SnapFrame(198));
        Assert.Equal(150, timeline.SnapFrame(150));
        Assert.Null(timeline.SnapIndicator);
        Assert.Equal(57, timeline.SnapFrame(57, new HashSet<ItemId> { shape.Id })); // 自分の端には合わせない

        timeline.Snap = false;
        Assert.Equal(57, timeline.SnapFrame(57));
        Assert.False(session.Settings.Snap);
    });

    [Fact]
    public Task SplitsDeletesCopiesAndGroups() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var timeline = new TimelineViewModel(session, _dialogs);
        var a = Shape(session, 0, 0, 60);
        var b = Shape(session, 1, 30, 60);
        var c = Shape(session, 0, 60, 30);

        // 選んでいなければ、再生位置にかかっているすべてを分割
        session.CurrentFrame = 45;
        await timeline.SplitCommand.ExecuteAsync(null);
        Assert.Equal(5, session.Scene.Items.Count);

        // 詰めて削除
        session.Selection = [a.Id];
        await timeline.RippleDeleteCommand.ExecuteAsync(null);
        Assert.Equal(15, Get(session, c.Id).Start);

        // コピーして、再生位置に貼り付ける（貼り付けたものを選ぶ）
        session.Selection = [c.Id];
        timeline.CopyCommand.Execute(null);
        session.CurrentFrame = 300;
        await timeline.PasteCommand.ExecuteAsync(null);
        var pasted = Get(session, Assert.Single(session.Selection));
        Assert.Equal((300L, c.Layer), (pasted.Start, pasted.Layer));

        // 複製とグループ
        session.Selection = [b.Id, c.Id];
        await timeline.GroupCommand.ExecuteAsync(null);
        Assert.NotNull(Get(session, b.Id).Group);
        await timeline.UngroupCommand.ExecuteAsync(null);
        Assert.Null(Get(session, b.Id).Group);
        var count = session.Scene.Items.Count;
        session.Selection = [c.Id];
        await timeline.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal(count + 1, session.Scene.Items.Count);
        Assert.Equal(Get(session, c.Id).End, Get(session, Assert.Single(session.Selection)).Start);

        timeline.SelectAllCommand.Execute(null);
        Assert.Equal(session.Scene.Items.Count, session.Selection.Count);
        await timeline.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(session.Scene.Items);
        Assert.False(timeline.DeleteCommand.CanExecute(null));

        // 分割できる位置にアイテムがなければ知らせる
        await timeline.SplitCommand.ExecuteAsync(null);
        Assert.Single(_dialogs.Errors);
    });

    [Fact]
    public Task KeepsTheLastValidStateWhileDragging() => Ui.Run(() =>
    {
        using var session = _environment.CreateSession();
        var timeline = new TimelineViewModel(session, _dialogs);
        var moving = Shape(session, 0, 0, 30);
        Shape(session, 0, 100, 30);
        session.Selection = [moving.Id];

        timeline.BeginPreviewEdit();
        Assert.True(timeline.UpdatePreviewEdit((project, scene) => EditCommands.MoveItems(project, scene, [moving.Id], 50)));
        Assert.False(timeline.UpdatePreviewEdit((project, scene) => EditCommands.MoveItems(project, scene, [moving.Id], 90))); // 重なる
        Assert.Equal(50, Get(session, moving.Id).Start);
        Assert.True(timeline.UpdatePreviewEdit((project, scene) => EditCommands.MoveItems(project, scene, [moving.Id], 200)));
        timeline.EndPreviewEdit();

        Assert.Equal(200, Get(session, moving.Id).Start);
        session.Document.Undo();
        Assert.Equal(0, Get(session, moving.Id).Start);
        Assert.False(session.Document.CanUndo && session.Document.UndoDescription == "移動");
    });

    [Fact]
    public Task EditsLayersMarkersAndRanges() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var timeline = new TimelineViewModel(session, _dialogs);
        var layers = session.Scene.Layers.Count;
        var first = session.Scene.Layers[0].Id;
        Shape(session, 0, 0, 30);

        timeline.ToggleLayer(first, LayerToggle.Muted);
        timeline.ToggleLayer(first, LayerToggle.Visible);
        Assert.Equal((true, false), (session.Scene.Layers[0].Muted, session.Scene.Layers[0].Visible));

        _dialogs.Prompts.Enqueue("背景");
        await timeline.RenameLayerCommand.ExecuteAsync(first);
        Assert.Equal("背景", session.Scene.Layers[0].Name);

        await timeline.MoveLayerDownCommand.ExecuteAsync(first);
        Assert.Equal(1, session.Scene.LayerIndex(first));
        timeline.AddLayerCommand.Execute(0);
        Assert.Equal(layers + 1, session.Scene.Layers.Count);

        // アイテムのあるレイヤーは、確認してから消す
        _dialogs.Confirmations.Enqueue(false);
        await timeline.RemoveLayerCommand.ExecuteAsync(first);
        Assert.Equal(layers + 1, session.Scene.Layers.Count);
        _dialogs.Confirmations.Enqueue(true);
        await timeline.RemoveLayerCommand.ExecuteAsync(first);
        Assert.Equal(layers, session.Scene.Layers.Count);
        Assert.Empty(session.Scene.Items);

        // マーカーは同じ位置でもう一度押すと外れる
        session.CurrentFrame = 40;
        timeline.AddMarkerCommand.Execute(null);
        Assert.Equal(40, Assert.Single(session.Scene.Markers).Frame);
        _dialogs.Prompts.Enqueue("サビ");
        await timeline.RenameMarkerCommand.ExecuteAsync(session.Scene.Markers[0]);
        Assert.Equal("サビ", session.Scene.Markers[0].Name);
        timeline.AddMarkerCommand.Execute(null);
        Assert.Empty(session.Scene.Markers);

        // 範囲
        timeline.SetRangeStartCommand.Execute(null);
        session.CurrentFrame = 90;
        timeline.SetRangeEndCommand.Execute(null);
        Assert.Equal((40L, 90L), (session.RangeStart, session.RangeEnd));
        session.CurrentFrame = 120;
        timeline.SetRangeStartCommand.Execute(null); // 終わりより後ろなら終わりを外す
        Assert.Equal((120L, (long?)null), (session.RangeStart, session.RangeEnd));
        timeline.ClearRangeCommand.Execute(null);
        Assert.Null(session.RangeStart);
    });

    [Fact]
    public Task EditsWithTheMouse() => Ui.Run(() =>
    {
        using var session = _environment.CreateSession();
        var timeline = new TimelineViewModel(session, _dialogs) { PixelsPerFrame = 4 };
        var control = new TimelineControl { ViewModel = timeline };
        var window = new Window { Width = 1000, Height = 400, Content = control };
        window.Show();
        var shape = Shape(session, 0, 10, 20);
        var next = Shape(session, 0, 30, 20);
        double X(long frame) => timeline.FrameToX(frame);
        var y = TimelineViewModel.RulerHeight + TimelineViewModel.LayerHeight / 2;

        // 押して選び、右へ 20 フレーム、下のレイヤーへドラッグ
        window.MouseDown(new Point(X(15), y), MouseButton.Left);
        window.MouseMove(new Point(X(35), y + TimelineViewModel.LayerHeight));
        window.MouseUp(new Point(X(35), y + TimelineViewModel.LayerHeight), MouseButton.Left);
        Assert.Equal([shape.Id], session.Selection);
        Assert.Equal((30L, session.Scene.Layers[1].Id), (Get(session, shape.Id).Start, Get(session, shape.Id).Layer));

        // 右の端をドラッグして長さを変える
        var row1 = y + TimelineViewModel.LayerHeight;
        window.MouseDown(new Point(X(50) - 2, row1), MouseButton.Left);
        window.MouseMove(new Point(X(70), row1));
        window.MouseUp(new Point(X(70), row1), MouseButton.Left);
        Assert.Equal(40, Get(session, shape.Id).Length);

        // Alt で隣との境目を動かす（ロール）
        window.MouseDown(new Point(X(50) - 2, y), MouseButton.Left, RawInputModifiers.Alt);
        Assert.Equal([next.Id], session.Selection);
        window.MouseMove(new Point(X(40), y), RawInputModifiers.Alt);
        window.MouseUp(new Point(X(40), y), MouseButton.Left, RawInputModifiers.Alt);
        Assert.Equal((30L, 10L), (Get(session, next.Id).Start, Get(session, next.Id).Length));

        // 何もないところをクリックすると、選択を外して再生位置を動かす
        window.MouseDown(new Point(X(200), y + TimelineViewModel.LayerHeight * 3), MouseButton.Left);
        window.MouseUp(new Point(X(200), y + TimelineViewModel.LayerHeight * 3), MouseButton.Left);
        Assert.Empty(session.Selection);
        Assert.Equal(200, session.CurrentFrame);

        // 範囲を囲んで選ぶ
        window.MouseDown(new Point(X(5), TimelineViewModel.RulerHeight + 1), MouseButton.Left);
        window.MouseMove(new Point(X(60), TimelineViewModel.RulerHeight + TimelineViewModel.LayerHeight * 2 - 1));
        window.MouseUp(new Point(X(60), TimelineViewModel.RulerHeight + TimelineViewModel.LayerHeight * 2 - 1), MouseButton.Left);
        Assert.Equal(2, session.Selection.Count);

        // 定規を押すと再生位置
        window.MouseDown(new Point(X(77), 10), MouseButton.Left);
        window.MouseUp(new Point(X(77), 10), MouseButton.Left);
        Assert.Equal(77, session.CurrentFrame);

        // レイヤーの見出しのボタン
        var header = TimelineViewModel.HeaderWidth - 4 - 4 * 20 + 9; // 表示
        window.MouseDown(new Point(header, y), MouseButton.Left);
        window.MouseUp(new Point(header, y), MouseButton.Left);
        Assert.False(session.Scene.Layers[0].Visible);

        window.MouseWheel(new Point(X(77), y), new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(5, timeline.PixelsPerFrame);
        window.Close();
    });
}
