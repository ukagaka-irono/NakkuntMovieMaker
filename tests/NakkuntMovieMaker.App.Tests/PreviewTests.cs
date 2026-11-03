using System.Numerics;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>プレビュー：表示、アイテムの選択とドラッグ（移動・拡大・回転）、J/K/L、タイムコード</summary>
public sealed class PreviewTests : IDisposable
{
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    // 1920×1080 の中央に 200×100 の四角形（0〜60 フレーム）
    private static ShapeItem AddRectangle(EditorSession session)
    {
        var shape = new ShapeItem(ItemId.New(), session.Scene.Layers[0].Id, 0, 60, new ShapeStyle(ShapeKind.Rectangle, 200, 100, new SolidPaint(RgbaColor.White)));
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, shape));
        return shape;
    }

    // 表示するフレームが描き終わるまで、画面の更新を進める
    private static async Task WaitForFrameAsync(PreviewViewModel preview)
    {
        var shown = false;
        preview.FrameShown += (_, _) => shown = true;
        for (var i = 0; i < 400 && !shown; i++)
        {
            preview.Tick();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(shown, "フレームが表示されませんでした");
    }

    private static VisualItem Item(EditorSession session, ItemId id) => (VisualItem)session.Scene.GetItem(id);

    [Fact]
    public Task ShowsFramesAtThePreviewScale() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var preview = new PreviewViewModel(session);
        AddRectangle(session);

        await WaitForFrameAsync(preview);

        Assert.Equal((960, 540), (preview.Bitmap!.PixelSize.Width, preview.Bitmap.PixelSize.Height));
        preview.ScaleChoice = PreviewViewModel.ScaleChoices[2];
        await WaitForFrameAsync(preview);
        Assert.Equal(480, preview.Bitmap!.PixelSize.Width);
        Assert.Equal(0.25, session.Settings.PreviewScale);
    });

    [Fact]
    public Task SelectsAndMovesItemsByDragging() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var preview = new PreviewViewModel(session);
        var shape = AddRectangle(session);
        await WaitForFrameAsync(preview);

        // 何もないところを押すと選択を外す
        session.Selection = [shape.Id];
        preview.BeginDrag(new Vector2(10, 10), PreviewDragMode.Move, addToSelection: false);
        preview.EndDrag();
        Assert.Empty(session.Selection);

        preview.BeginDrag(new Vector2(960, 540), PreviewDragMode.Move, addToSelection: false);
        Assert.Equal([shape.Id], session.Selection);
        preview.UpdateDrag(new Vector2(1000, 560), constrain: false);
        Assert.Equal((40.0, 20.0), (Item(session, shape.Id).Transform.X.Value, Item(session, shape.Id).Transform.Y.Value));
        preview.UpdateDrag(new Vector2(1030, 550), constrain: true); // Shift で横だけ
        preview.EndDrag();
        Assert.Equal((70.0, 0.0), (Item(session, shape.Id).Transform.X.Value, Item(session, shape.Id).Transform.Y.Value));

        // ドラッグ全体で 1 回の操作
        session.Document.Undo();
        Assert.Equal(0.0, Item(session, shape.Id).Transform.X.Value);
        Assert.Equal("移動", session.Document.RedoDescription);
    });

    [Fact]
    public Task ScalesAndRotatesWithHandles() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var preview = new PreviewViewModel(session);
        var shape = AddRectangle(session);
        session.Selection = [shape.Id];
        await WaitForFrameAsync(preview);

        // 右下の角（1060, 590）を中心から 2 倍の距離へ
        preview.BeginDrag(new Vector2(1060, 590), PreviewDragMode.Scale, addToSelection: false);
        preview.UpdateDrag(new Vector2(1160, 640), constrain: false);
        preview.EndDrag();
        Assert.Equal((2.0, 2.0), (Item(session, shape.Id).Transform.ScaleX.Value, Item(session, shape.Id).Transform.ScaleY.Value), new ToleranceComparer(1e-6));

        // 上の丸を、中心のまわりに右へ 90 度
        preview.BeginDrag(new Vector2(960, 400), PreviewDragMode.Rotate, addToSelection: false);
        preview.UpdateDrag(new Vector2(1100, 540), constrain: false);
        preview.EndDrag();
        Assert.Equal(90.0, Item(session, shape.Id).Transform.Rotation.Value, 6);
    });

    [Fact]
    public void ShiftsEveryKeyframeWhenMoving()
    {
        var shape = new ShapeItem(ItemId.New(), LayerId.New(), 0, 60, new ShapeStyle(ShapeKind.Rectangle, 10, 10, new SolidPaint(RgbaColor.White)))
        {
            Transform = Transform2D.Identity with { X = Animatable<double>.FromKeys([new Keyframe<double>(0, 0), new Keyframe<double>(30, 100)]) },
        };

        var moved = PreviewGeometry.Move(shape, 10, 5);
        var scaled = PreviewGeometry.Scale(PreviewGeometry.Rotate(shape, 370), 0.5);

        Assert.Equal([10.0, 110.0], moved.Transform.X.Keys.Select(key => key.Value));
        Assert.Equal(5.0, moved.Transform.Y.Value);
        Assert.Equal((370.0, 0.5), (scaled.Transform.Rotation.Value, scaled.Transform.ScaleX.Value));
        Assert.Equal(-90, PreviewGeometry.AngleBetween(Vector2.Zero, new Vector2(1, 0), new Vector2(0, -1)), 6);
        Assert.Equal(180, Math.Abs(PreviewGeometry.AngleBetween(Vector2.Zero, new Vector2(1, 0), new Vector2(-1, 0.0001f))), 1);
    }

    [Fact]
    public void HitTestPrefersTheSelectedItemThenTheTopOne()
    {
        var below = new ItemOutline(ItemId.New(), Matrix3x2.CreateTranslation(100, 100), 100, 100);
        var above = new ItemOutline(ItemId.New(), Matrix3x2.CreateRotation(MathF.PI / 4) * Matrix3x2.CreateTranslation(120, 100), 100, 20);

        Assert.Equal(above.Item, PreviewGeometry.HitTest([below, above], new Vector2(120, 100)));
        Assert.Equal(below.Item, PreviewGeometry.HitTest([below, above], new Vector2(120, 100), new HashSet<ItemId> { below.Item }));
        Assert.Equal(below.Item, PreviewGeometry.HitTest([below, above], new Vector2(140, 100))); // 斜めの細い枠からは外れる
        Assert.Null(PreviewGeometry.HitTest([below, above], new Vector2(10, 10)));
    }

    [Fact]
    public Task ShuttlesWithJkl() => Ui.Run(() =>
    {
        using var session = _environment.CreateSession();
        var preview = new PreviewViewModel(session);
        AddRectangle(session);

        preview.ForwardCommand.Execute(null);
        Assert.True(session.Playback.IsPlaying);
        preview.ForwardCommand.Execute(null);
        Assert.Equal((2, "2×", false), (preview.Speed, preview.SpeedText, session.Playback.IsPlaying));
        preview.BackwardCommand.Execute(null);
        preview.BackwardCommand.Execute(null);
        Assert.Equal(-2, preview.Speed);
        preview.StopCommand.Execute(null);
        Assert.False(preview.IsPlaying);

        // コマ送りと先頭・末尾
        preview.NextFrameCommand.Execute(null);
        preview.NextFrameCommand.Execute(null);
        preview.PreviousFrameCommand.Execute(null);
        Assert.Equal(1, session.CurrentFrame);
        preview.GoToEndCommand.Execute(null);
        Assert.Equal(59, session.CurrentFrame);
        preview.GoToStartCommand.Execute(null);
        preview.PreviousFrameCommand.Execute(null);
        Assert.Equal(0, session.CurrentFrame);
    });

    [Theory]
    [InlineData(0, 30, "00:00:00.00")]
    [InlineData(45, 30, "00:00:01.15")]
    [InlineData(108000, 30, "01:00:00.00")]
    public void FormatsTimecodes(long frame, int fps, string expected) =>
        Assert.Equal(expected, PreviewViewModel.FormatTimecode(frame, fps));

    [Fact]
    public void FormatsTimecodesAtNtscRates() =>
        Assert.Equal("00:00:01.00", PreviewViewModel.FormatTimecode(30, Rational.Fps29_97)); // 30 フレーム目は 1.001 秒

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < tolerance && Math.Abs(x.Item2 - y.Item2) < tolerance;

        public int GetHashCode((double, double) obj) => 0;
    }
}
