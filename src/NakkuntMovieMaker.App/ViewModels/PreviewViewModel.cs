using System.Diagnostics;
using System.Numerics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Playback;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>プレビューの解像度の候補</summary>
public sealed record PreviewScaleChoice(string Name, double Scale)
{
    public override string ToString() => Name;
}

/// <summary>
/// プレビュー。再生・停止・シーク・コマ送り・J/K/L、解像度の切り替え、グリッドとセーフエリア、
/// プレビュー上でのアイテムの選択と、ドラッグでの移動・拡大・回転。
/// 等速の再生は音の時計で進め、それ以外の速さ（早送り・逆再生）は音を出さずに位置だけ進める
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private const int MaxShuttleSpeed = 8;

    private readonly EditorSession _session;
    private readonly Stopwatch _shuttleClock = new();
    private long _shownFrame = -1;
    private bool _dirty = true;
    private Task? _request;
    private double _shuttlePosition;

    // ドラッグ中の編集（ドラッグの間は、前の編集を元に戻してから適用し直すので、終わったときに 1 回の操作になる）
    private DragState? _drag;

    public PreviewViewModel(EditorSession session)
    {
        _session = session;
        ScaleChoice = ScaleChoices.FirstOrDefault(choice => choice.Scale == session.Settings.PreviewScale) ?? ScaleChoices[1];
        ShowGrid = session.Settings.ShowGrid;
        ShowSafeArea = session.Settings.ShowSafeArea;
        session.Changed += (_, _) => _dirty = true;
        session.ProjectOpened += (_, _) =>
        {
            Speed = 0;
            _shownFrame = -1;
            _dirty = true;
            session.Frames.Scale = ScaleChoice.Scale;
            AttachPlayback();
            OnPropertyChanged(nameof(Timecode));
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorSession.CurrentFrame))
            {
                OnPropertyChanged(nameof(Timecode));
            }
            else if (e.PropertyName is nameof(EditorSession.Selection))
            {
                FrameShown?.Invoke(this, EventArgs.Empty);
            }
        };
        AttachPlayback();
    }

    /// <summary>表示するフレームが変わった（または選択の枠を描き直す）</summary>
    public event EventHandler? FrameShown;

    /// <summary>全画面プレビューを開く・閉じる（画面が行う）</summary>
    public event EventHandler? FullScreenRequested;

    public static IReadOnlyList<PreviewScaleChoice> ScaleChoices { get; } =
    [
        new("フル", 1),
        new("1/2", 0.5),
        new("1/4", 0.25),
    ];

    public EditorSession Session => _session;

    /// <summary>表示しているフレームの画像（大きさはプレビューの解像度）</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>表示しているフレームの描画の計画（アイテムの枠を求めるのに使う）</summary>
    public FramePlan? Plan { get; private set; }

    public SceneFormat Format => _session.Scene.Format;

    /// <summary>再生の速さ。0 は停止、1 は等速（音あり）、負は逆再生</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(SpeedText))]
    public partial int Speed { get; private set; }

    public bool IsPlaying => Speed != 0;

    /// <summary>今鳴っている音の大きさ（レベルメーター）</summary>
    [ObservableProperty]
    public partial Audio.StereoLevel Level { get; private set; } = Audio.StereoLevel.Silent;

    public string SpeedText => Speed is 0 or 1 ? "" : $"{Speed}×";

    [ObservableProperty]
    public partial PreviewScaleChoice ScaleChoice { get; set; }

    [ObservableProperty]
    public partial bool ShowGrid { get; set; }

    [ObservableProperty]
    public partial bool ShowSafeArea { get; set; }

    /// <summary>再生位置（時:分:秒.フレーム）</summary>
    public string Timecode => FormatTimecode(_session.CurrentFrame, _session.Scene.Fps);

    public static string FormatTimecode(long frame, Rational fps)
    {
        var rate = Math.Max(1, (int)Math.Round(fps.ToDouble()));
        var seconds = (long)Math.Floor(TimeMath.FrameToSeconds(frame, fps).ToDouble());
        var rest = frame - TimeMath.SecondsToFrame(seconds, fps, Rounding.Up);
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}.{Math.Clamp(rest, 0, rate - 1):00}";
    }

    /// <summary>画面の更新のたびに呼ぶ。再生中は時計の位置のフレームを、止まっているときは再生位置のフレームを表示する</summary>
    public void Tick()
    {
        if (_session.IsClosed)
        {
            return;
        }

        var playback = _session.Playback;
        Level = Speed == 1 ? playback.Level : Audio.StereoLevel.Silent;
        if (Speed == 1)
        {
            if (playback.TryGetFrame(out var frame))
            {
                Show(frame);
            }

            _session.CurrentFrame = playback.CurrentFrame;
            return;
        }

        if (Speed != 0)
        {
            AdvanceShuttle();
        }

        var current = _session.CurrentFrame;
        if ((current != _shownFrame || _dirty) && _request is null or { IsCompleted: true })
        {
            _dirty = false;
            _request = RequestAsync(current);
        }
    }

    /// <summary>表示しているフレームのアイテムの枠</summary>
    public IReadOnlyList<ItemOutline> Outlines() =>
        Plan is { } plan ? PreviewGeometry.Outlines(plan, _session.Backend, _session.Media) : [];

    [RelayCommand]
    private void TogglePlay()
    {
        if (Speed == 0)
        {
            SetSpeed(1);
        }
        else
        {
            SetSpeed(0);
        }
    }

    [RelayCommand]
    private void Stop() => SetSpeed(0);

    /// <summary>L：止まっていれば等速で再生し、再生中なら速める</summary>
    [RelayCommand]
    private void Forward() => SetSpeed(Speed <= 0 ? 1 : Math.Min(Speed * 2, MaxShuttleSpeed));

    /// <summary>J：逆方向に再生し、もう一度押すと速める</summary>
    [RelayCommand]
    private void Backward() => SetSpeed(Speed >= 0 ? -1 : Math.Max(Speed * 2, -MaxShuttleSpeed));

    [RelayCommand]
    private void PreviousFrame() => Step(-1);

    [RelayCommand]
    private void NextFrame() => Step(1);

    [RelayCommand]
    private void GoToStart() => SeekTo(0);

    [RelayCommand]
    private void GoToEnd() => SeekTo(Math.Max(0, _session.Scene.Duration - 1));

    [RelayCommand]
    private void ToggleFullScreen() => FullScreenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>プレビュー上を押した（シーンの座標）。アイテムを選び、ドラッグを始める。mode は押した場所（枠の角なら拡大、上の丸なら回転）</summary>
    public void BeginDrag(Vector2 point, PreviewDragMode mode, bool addToSelection)
    {
        var outlines = Outlines();
        var selected = _session.Selection;
        ItemId? target = mode == PreviewDragMode.Move ? PreviewGeometry.HitTest(outlines, point, selected) : selected.FirstOrDefault();
        if (target is not { } id || !_session.Scene.Items.TryGetValue(id, out var item) || item is not VisualItem visual)
        {
            if (!addToSelection)
            {
                _session.Selection = [];
            }

            return;
        }

        if (!selected.Contains(id))
        {
            _session.Selection = addToSelection ? selected.Add(id) : [id];
        }

        var outline = outlines.LastOrDefault(candidate => candidate.Item == id);
        var pivot = new Vector2(
            (float)(_session.Scene.Format.Width / 2.0 + visual.Transform.X.Evaluate(_session.CurrentFrame - visual.Start)),
            (float)(_session.Scene.Format.Height / 2.0 + visual.Transform.Y.Evaluate(_session.CurrentFrame - visual.Start)));
        _drag = new DragState(mode, point, pivot, outline?.Center ?? pivot, [.. _session.SelectedItems.OfType<VisualItem>()], false);
    }

    /// <summary>ドラッグ中（シーンの座標）。Shift を押していれば、移動を縦か横にそろえる</summary>
    public void UpdateDrag(Vector2 point, bool constrain)
    {
        if (_drag is not { Items.Count: > 0 } drag)
        {
            return;
        }

        var delta = point - drag.Start;
        if (constrain && drag.Mode == PreviewDragMode.Move)
        {
            delta = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? delta with { Y = 0 } : delta with { X = 0 };
        }

        Func<VisualItem, VisualItem> change = drag.Mode switch
        {
            PreviewDragMode.Move => item => PreviewGeometry.Move(item, Math.Round(delta.X), Math.Round(delta.Y)),
            PreviewDragMode.Scale => item => PreviewGeometry.Scale(item, Math.Max(0.01, ScaleFactor(drag, point))),
            _ => item => PreviewGeometry.Rotate(item, Math.Round(PreviewGeometry.AngleBetween(drag.Pivot, drag.Start, point), 1)),
        };

        if (drag.Applied)
        {
            _session.Document.Undo();
        }

        var description = drag.Mode switch
        {
            PreviewDragMode.Move => "移動",
            PreviewDragMode.Scale => "拡大・縮小",
            _ => "回転",
        };
        var operations = drag.Items.Select(item => EditCommands.UpdateItem(_session.Project, _session.SceneId, change(item)));
        var applied = _session.Execute(Operations.Combine(description, operations)) is null;
        _drag = drag with { Applied = applied };
    }

    public void EndDrag() => _drag = null;

    partial void OnScaleChoiceChanged(PreviewScaleChoice value)
    {
        _session.Frames.Scale = value.Scale;
        _session.Settings.PreviewScale = value.Scale;
        _dirty = true;
    }

    partial void OnShowGridChanged(bool value)
    {
        _session.Settings.ShowGrid = value;
        FrameShown?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowSafeAreaChanged(bool value)
    {
        _session.Settings.ShowSafeArea = value;
        FrameShown?.Invoke(this, EventArgs.Empty);
    }

    private static double ScaleFactor(DragState drag, Vector2 point)
    {
        var before = Vector2.Distance(drag.Start, drag.Pivot);
        return before < 1 ? 1 : Vector2.Distance(point, drag.Pivot) / before;
    }

    private void AttachPlayback() =>
        _session.Playback.Ended += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (Speed == 1)
            {
                Speed = 0;
            }
        });

    private void SetSpeed(int speed)
    {
        var playback = _session.Playback;
        if (speed == 1)
        {
            playback.Seek(_session.CurrentFrame);
            playback.Play();
        }
        else if (Speed == 1)
        {
            playback.Pause();
            _session.CurrentFrame = playback.CurrentFrame;
        }

        _shuttlePosition = _session.CurrentFrame;
        _shuttleClock.Restart();
        Speed = speed;
    }

    // 早送り・逆再生：経った時間ぶん位置を進める（端まで行ったら止める）
    private void AdvanceShuttle()
    {
        var fps = _session.Scene.Fps.ToDouble();
        _shuttlePosition += Speed * fps * _shuttleClock.Elapsed.TotalSeconds;
        _shuttleClock.Restart();
        var end = Math.Max(0, _session.Scene.Duration - 1);
        if (_shuttlePosition <= 0 || _shuttlePosition >= end)
        {
            _shuttlePosition = Math.Clamp(_shuttlePosition, 0, end);
            Speed = 0;
        }

        _session.CurrentFrame = (long)_shuttlePosition;
    }

    private void Step(int frames)
    {
        SetSpeed(0);
        SeekTo(_session.CurrentFrame + frames);
    }

    private void SeekTo(long frame)
    {
        if (Speed != 0)
        {
            SetSpeed(0);
        }

        _session.Seek(frame);
    }

    private async Task RequestAsync(long frame)
    {
        try
        {
            var rendered = await _session.Frames.RequestAsync(frame);
            Show(rendered);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Show(RenderedFrame frame)
    {
        if (Bitmap is null || !Bitmaps.CopyInto(Bitmap, frame.Image))
        {
            Bitmap = Bitmaps.Create(frame.Image);
        }

        Plan = frame.Plan;
        _shownFrame = frame.Frame;
        FrameShown?.Invoke(this, EventArgs.Empty);
    }

    private sealed record DragState(PreviewDragMode Mode, Vector2 Start, Vector2 Pivot, Vector2 Center, IReadOnlyList<VisualItem> Items, bool Applied);
}

/// <summary>プレビュー上のドラッグの種類</summary>
public enum PreviewDragMode
{
    Move,
    Scale,
    Rotate,
}
