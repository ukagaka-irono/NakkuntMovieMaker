using System.Collections.Concurrent;
using System.Collections.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>
/// タイムライン。表示（ズーム・スクロール・波形とサムネイル）と、編集の操作一式（移動・トリム・リップル・ロール・分割・削除・
/// コピーと貼り付け・複製・グループ化）、レイヤーの操作、マーカー、範囲、スナップ。
/// 描画とマウスの操作は TimelineControl が行い、ここは座標の計算と編集を受け持つ
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    public const double RulerHeight = 28;
    public const double LayerHeight = 44;
    public const double HeaderWidth = 176;

    /// <summary>スナップする距離（ピクセル）</summary>
    public const double SnapDistance = 8;

    private const double MinPixelsPerFrame = 0.01;
    private const double MaxPixelsPerFrame = 48;

    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private readonly ConcurrentDictionary<string, AudioPeaks?> _peaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Bitmap?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private ClipboardContent? _clipboard;
    private bool _previewApplied;
    private Project? _previewBase;

    public TimelineViewModel(EditorSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        Snap = session.Settings.Snap;
        session.Changed += (_, _) => Invalidate();
        session.ProjectOpened += (_, _) =>
        {
            ScrollX = 0;
            ScrollY = 0;
            Invalidate();
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorSession.CurrentFrame) or nameof(EditorSession.Selection)
                or nameof(EditorSession.RangeStart) or nameof(EditorSession.RangeEnd))
            {
                Invalidated?.Invoke(this, EventArgs.Empty);
            }

            if (e.PropertyName is nameof(EditorSession.Selection))
            {
                NotifySelectionCommands();
            }
            else if (e.PropertyName is nameof(EditorSession.CurrentFrame) && !session.IsClosed && session.Playback.IsPlaying)
            {
                FollowPlayhead();
            }
        };
    }

    /// <summary>描き直す</summary>
    public event EventHandler? Invalidated;

    public EditorSession Session => _session;

    public Scene Scene => _session.Scene;

    /// <summary>1 フレームの幅（ピクセル）</summary>
    [ObservableProperty]
    public partial double PixelsPerFrame { get; set; } = 4;

    /// <summary>横のスクロール（ピクセル）</summary>
    [ObservableProperty]
    public partial double ScrollX { get; set; }

    /// <summary>縦のスクロール（ピクセル）</summary>
    [ObservableProperty]
    public partial double ScrollY { get; set; }

    /// <summary>表示している幅（ヘッダーを除く、ピクセル）。画面が入れる</summary>
    [ObservableProperty]
    public partial double ViewportWidth { get; set; } = 800;

    /// <summary>表示している高さ（定規を除く、ピクセル）。画面が入れる</summary>
    [ObservableProperty]
    public partial double ViewportHeight { get; set; } = 200;

    [ObservableProperty]
    public partial bool Snap { get; set; }

    /// <summary>横にスクロールできる幅（シーンの長さ＋余白）</summary>
    public double ContentWidth => Math.Max(Scene.Duration + Scene.Fps.ToDouble() * 30, 1) * PixelsPerFrame;

    public double ContentHeight => Scene.Layers.Count * LayerHeight;

    public double MaxScrollX => Math.Max(0, ContentWidth - ViewportWidth);

    public double MaxScrollY => Math.Max(0, ContentHeight - ViewportHeight);

    /// <summary>ドラッグ中にスナップした位置（描画用。なければ null）</summary>
    public long? SnapIndicator { get; set; }

    public double FrameToX(long frame) => HeaderWidth + frame * PixelsPerFrame - ScrollX;

    public double FrameToX(double frame) => HeaderWidth + frame * PixelsPerFrame - ScrollX;

    /// <summary>画面の x からフレーム（最も近いフレーム）</summary>
    public long XToFrame(double x) => Math.Max(0, (long)Math.Round((x - HeaderWidth + ScrollX) / PixelsPerFrame));

    public double LayerToY(int index) => RulerHeight + index * LayerHeight - ScrollY;

    /// <summary>画面の y からレイヤーの番号（範囲外なら -1 か Layers.Count）</summary>
    public int YToLayer(double y)
    {
        var index = (int)Math.Floor((y - RulerHeight + ScrollY) / LayerHeight);
        return Math.Clamp(index, -1, Scene.Layers.Count);
    }

    /// <summary>
    /// スナップした位置。スナップが入っていれば、近く（SnapDistance ピクセル以内）の再生位置・マーカー・範囲・
    /// ほかのアイテムの端に合わせる。exclude のアイテムの端は使わない
    /// </summary>
    public long SnapFrame(long frame, IReadOnlySet<ItemId>? exclude = null)
    {
        SnapIndicator = null;
        if (!Snap)
        {
            return frame;
        }

        var limit = SnapDistance / PixelsPerFrame;
        var candidates = Scene.Items.Values
            .Where(item => exclude?.Contains(item.Id) != true)
            .SelectMany(item => new[] { item.Start, item.End })
            .Concat(Scene.Markers.Select(marker => marker.Frame))
            .Append(_session.CurrentFrame)
            .Append(0);
        if (_session.RangeStart is { } start)
        {
            candidates = candidates.Append(start);
        }

        if (_session.RangeEnd is { } end)
        {
            candidates = candidates.Append(end);
        }

        var best = candidates.MinBy(candidate => Math.Abs(candidate - frame));
        if (Math.Abs(best - frame) <= limit)
        {
            SnapIndicator = best;
            return best;
        }

        return frame;
    }

    /// <summary>アイテムの表示名</summary>
    public string Label(Item item) => ItemLabels.Label(item, _session.Project);

    /// <summary>素材の波形（読み込み中・音声がなければ null。読み終えたら描き直す）</summary>
    public AudioPeaks? GetPeaks(string path)
    {
        if (_peaks.TryGetValue(path, out var peaks))
        {
            return peaks;
        }

        _peaks[path] = null;
        var cache = _session.Cache;
        _ = Task.Run(() =>
        {
            try
            {
                _peaks[path] = cache.GetPeaks(path);
            }
            catch (Exception ex) when (ex is MediaException or IOException or InvalidOperationException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => Invalidated?.Invoke(this, EventArgs.Empty));
        });
        return null;
    }

    /// <summary>素材の最初のフレームのサムネイル（読み込み中なら null）</summary>
    public Bitmap? GetThumbnail(string path)
    {
        if (_thumbnails.TryGetValue(path, out var thumbnail))
        {
            return thumbnail;
        }

        _thumbnails[path] = null;
        _ = Task.Run(() =>
        {
            BgraImage image;
            try
            {
                image = ThumbnailExtractor.Extract(path, Rational.Zero, 160, (int)LayerHeight);
            }
            catch (Exception ex) when (ex is MediaException or IOException or InvalidOperationException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _thumbnails[path] = Bitmaps.Create(image);
                Invalidated?.Invoke(this, EventArgs.Empty);
            });
        });
        return null;
    }

    /// <summary>アイテムの音（素材か声）のファイル。なければ null</summary>
    public string? AudioPath(Item item) => item switch
    {
        VideoItem video => AssetPath(video.Asset),
        AudioItem audio => AssetPath(audio.Asset),
        VoiceItem { Source: SynthesizedVoice { CacheKey: { } key } } => _session.Voices.Cache.PathOf(key) is var path && File.Exists(path) ? path : null,
        VoiceItem { Source: RecordedVoice recorded } => AssetPath(recorded.Asset),
        _ => null,
    };

    public string? AssetPath(AssetId id) =>
        _session.Project.Assets.TryGetValue(id, out var asset) && AssetLinks.Resolve(asset, _session.ProjectFolder) is var path && File.Exists(path) ? path : null;

    /// <summary>ドラッグ中の編集を始める（UpdatePreviewEdit を何度呼んでも、最後に 1 回の操作として残る）</summary>
    public void BeginPreviewEdit()
    {
        _previewApplied = false;
        _previewBase = _session.Project;
    }

    /// <summary>
    /// ドラッグ中の編集を、ドラッグを始めたときの状態から作り直して適用する。作れない（重なるなど）ときは、
    /// 前に適用できた状態のままにして false を返す
    /// </summary>
    public bool UpdatePreviewEdit(Func<Project, SceneId, IOperation> build)
    {
        if (_previewBase is not { } baseProject)
        {
            return false;
        }

        IOperation operation;
        try
        {
            operation = build(baseProject, _session.SceneId);
        }
        catch (EditException)
        {
            return false;
        }

        if (_previewApplied)
        {
            _session.Document.Undo();
        }

        _previewApplied = _session.Execute(operation) is null;
        return _previewApplied;
    }

    public void EndPreviewEdit()
    {
        _previewApplied = false;
        _previewBase = null;
        SnapIndicator = null;
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>OS からドロップしたファイルを素材に加え、その位置に順に置く</summary>
    public async Task DropFilesAsync(IReadOnlyList<string> paths, long frame, int layer)
    {
        var failures = new List<string>();
        var (operation, assets) = MediaItems.Import(_session.Project, _session.ProjectFolder, paths, failures);
        if (operation is not null)
        {
            _session.Execute(operation);
        }

        foreach (var asset in assets)
        {
            await DropAssetAsync(asset.Id, frame, layer);
            frame = _session.SelectedItems.FirstOrDefault()?.End ?? frame;
        }

        if (failures.Count > 0)
        {
            await _dialogs.ShowErrorAsync("追加できないファイルがあります", string.Join("\n", failures));
        }
    }

    /// <summary>再生中に再生位置が見えなくなったら、見えるところまでスクロールする</summary>
    public void FollowPlayhead()
    {
        var x = _session.CurrentFrame * PixelsPerFrame;
        if (x < ScrollX || x > ScrollX + ViewportWidth - 16)
        {
            ScrollX = Math.Clamp(x - ViewportWidth * 0.1, 0, Math.Max(MaxScrollX, x));
        }
    }

    /// <summary>素材の一覧からドロップした素材を、その位置に置く（レイヤーが埋まっていれば上の空いているレイヤー）</summary>
    public async Task DropAssetAsync(AssetId id, long frame, int layer)
    {
        if (!_session.Project.Assets.TryGetValue(id, out var asset))
        {
            return;
        }

        var (operation, item) = MediaItems.Place(_session.Project, _session.SceneId, _session.ProjectFolder, asset, SnapFrame(frame), Math.Max(0, layer));
        SnapIndicator = null;
        if (_session.Execute(operation) is { } error)
        {
            await _dialogs.ShowErrorAsync("置けません", error);
            return;
        }

        _session.Selection = [item.Id];
    }

    /// <summary>位置 x を中心にズームする（factor 倍）</summary>
    public void ZoomAt(double factor, double x)
    {
        var frame = (x - HeaderWidth + ScrollX) / PixelsPerFrame;
        PixelsPerFrame = Math.Clamp(PixelsPerFrame * factor, MinPixelsPerFrame, MaxPixelsPerFrame);
        ScrollX = Math.Clamp(frame * PixelsPerFrame - (x - HeaderWidth), 0, MaxScrollX);
    }

    /// <summary>テキスト・図形を置くときの長さ（秒）</summary>
    public const int NewItemSeconds = 5;

    /// <summary>図形の選択肢（背景はシーン全体を塗る）</summary>
    public static IReadOnlyList<ShapeChoice> ShapeChoices { get; } =
        [.. Enum.GetValues<ShapeKind>().Select(kind => new ShapeChoice(kind, ItemLabels.ShapeName(kind)))];

    /// <summary>再生位置の空いているレイヤーにテキストを置く（プリセットを選べば、そのスタイルで）</summary>
    [RelayCommand]
    private Task AddTextAsync(TextPreset? preset)
    {
        var style = preset?.Style ?? TextStyle.Default;
        var text = new TextItem(ItemId.New(), default, _session.CurrentFrame, NewItemLength(), "テキスト") { Style = style };
        return PlaceAsync(text, "テキストの追加");
    }

    /// <summary>再生位置の空いているレイヤーに図形を置く</summary>
    [RelayCommand]
    private Task AddShapeAsync(ShapeChoice? choice)
    {
        var kind = choice?.Kind ?? ShapeKind.Rectangle;
        var (width, height) = kind switch
        {
            ShapeKind.Background => (Scene.Format.Width, Scene.Format.Height),
            ShapeKind.Line or ShapeKind.Arrow => (400.0, 40.0),
            _ => (300.0, 300.0),
        };
        var fill = kind == ShapeKind.Background ? new SolidPaint(new RgbaColor(40, 44, 52)) : new SolidPaint(new RgbaColor(74, 210, 255));
        var shape = new ShapeItem(ItemId.New(), default, _session.CurrentFrame, NewItemLength(), new ShapeStyle(kind, width, height, fill));

        // 背景はいちばん下のレイヤーから空きを探す。ほかは選んでいるアイテムの上のレイヤーから
        return PlaceAsync(shape, "図形の追加", kind == ShapeKind.Background ? 0 : null);
    }

    /// <summary>シーン全体が見えるようにズームする</summary>
    [RelayCommand]
    private void ZoomToFit()
    {
        PixelsPerFrame = Math.Clamp(ViewportWidth / Math.Max(1, Scene.Duration * 1.05), MinPixelsPerFrame, MaxPixelsPerFrame);
        ScrollX = 0;
    }

    [RelayCommand]
    private void ZoomIn() => ZoomAt(1.5, FrameToX(_session.CurrentFrame));

    [RelayCommand]
    private void ZoomOut() => ZoomAt(1 / 1.5, FrameToX(_session.CurrentFrame));

    [RelayCommand]
    private void ToggleSnap() => Snap = !Snap;

    [RelayCommand]
    private Task SplitAsync() => RunAsync("分割できません", (project, scene) =>
        EditCommands.Split(project, scene, Targets(project, scene, _session.CurrentFrame), _session.CurrentFrame));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => RunAsync("削除できません", (project, scene) => EditCommands.DeleteItems(project, scene, _session.Selection));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RippleDeleteAsync() => RunAsync("削除できません", (project, scene) => EditCommands.RippleDelete(project, scene, _session.Selection));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        _clipboard = EditCommands.Copy(_session.Project, _session.SceneId, _session.Selection);
        PasteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CutAsync()
    {
        Copy();
        await DeleteAsync();
    }

    /// <summary>再生位置に貼り付ける（レイヤーは、選んでいるアイテムのいちばん下のレイヤー、なければコピーしたときのレイヤー）</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (_clipboard is not { } clipboard)
        {
            return;
        }

        var scene = Scene;
        var layer = _session.SelectedItems.Count > 0
            ? _session.SelectedItems.Min(item => scene.LayerIndex(item.Layer))
            : clipboard.Entries.Min(entry => Math.Max(0, scene.LayerIndex(entry.Item.Layer)));
        var before = scene.Items.Keys.ToHashSet();
        if (await RunAsync("貼り付けられません", (project, id) => EditCommands.Paste(project, id, clipboard, _session.CurrentFrame, layer)))
        {
            _session.Selection = Scene.Items.Keys.Where(id => !before.Contains(id)).ToImmutableHashSet();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DuplicateAsync()
    {
        var before = Scene.Items.Keys.ToHashSet();
        if (await RunAsync("複製できません", (project, scene) => EditCommands.Duplicate(project, scene, _session.Selection)))
        {
            _session.Selection = Scene.Items.Keys.Where(id => !before.Contains(id)).ToImmutableHashSet();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task GroupAsync() => RunAsync("グループにできません", (project, scene) => EditCommands.GroupItems(project, scene, _session.Selection));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task UngroupAsync() => RunAsync("グループを解除できません", (project, scene) => EditCommands.UngroupItems(project, scene, _session.Selection));

    [RelayCommand]
    private void SelectAll() => _session.Selection = [.. Scene.Items.Keys];

    /// <summary>再生位置にマーカーを置く（すでにあれば外す）</summary>
    [RelayCommand]
    private void AddMarker()
    {
        var frame = _session.CurrentFrame;
        if (Scene.Markers.FirstOrDefault(marker => marker.Frame == frame) is { } existing)
        {
            _session.Execute(new SetMarker(_session.SceneId, existing.Id, null));
            return;
        }

        var marker = new Marker(MarkerId.New(), frame, $"マーカー {Scene.Markers.Count + 1}", new RgbaColor(74, 210, 255));
        _session.Execute(new SetMarker(_session.SceneId, marker.Id, marker));
    }

    [RelayCommand]
    private async Task RenameMarkerAsync(Marker marker)
    {
        if (await _dialogs.PromptAsync("マーカーの名前", "名前", marker.Name) is { Length: > 0 } name)
        {
            _session.Execute(new SetMarker(_session.SceneId, marker.Id, marker with { Name = name }));
        }
    }

    [RelayCommand]
    private void RemoveMarker(Marker marker) => _session.Execute(new SetMarker(_session.SceneId, marker.Id, null));

    [RelayCommand]
    private void SetRangeStart()
    {
        _session.RangeStart = _session.CurrentFrame;
        if (_session.RangeEnd <= _session.RangeStart)
        {
            _session.RangeEnd = null;
        }
    }

    [RelayCommand]
    private void SetRangeEnd()
    {
        _session.RangeEnd = Math.Max(_session.CurrentFrame, 1);
        if (_session.RangeStart >= _session.RangeEnd)
        {
            _session.RangeStart = null;
        }
    }

    [RelayCommand]
    private void ClearRange()
    {
        _session.RangeStart = null;
        _session.RangeEnd = null;
    }

    /// <summary>レイヤーを index の位置に足す</summary>
    [RelayCommand]
    private void AddLayer(int index) =>
        _session.Execute(EditCommands.AddLayer(_session.Project, _session.SceneId, Math.Clamp(index, 0, Scene.Layers.Count), $"レイヤー {Scene.Layers.Count + 1}"));

    [RelayCommand]
    private async Task RemoveLayerAsync(LayerId layer)
    {
        var count = Scene.ItemsOnLayer(layer).Count();
        if (count > 0 && !await _dialogs.ConfirmAsync("レイヤーの削除", $"このレイヤーのアイテム {count} 個も消えます。削除しますか？", "削除"))
        {
            return;
        }

        await RunAsync("レイヤーを削除できません", (project, scene) => EditCommands.RemoveLayer(project, scene, layer));
    }

    [RelayCommand]
    private Task MoveLayerUpAsync(LayerId layer) => MoveLayerAsync(layer, -1);

    [RelayCommand]
    private Task MoveLayerDownAsync(LayerId layer) => MoveLayerAsync(layer, 1);

    [RelayCommand]
    private async Task RenameLayerAsync(LayerId id)
    {
        var layer = Scene.GetLayer(id);
        if (await _dialogs.PromptAsync("レイヤーの名前", "名前", layer.Name) is { Length: > 0 } name)
        {
            UpdateLayer(layer with { Name = name });
        }
    }

    /// <summary>レイヤーの表示・ロック・ミュート・ソロを切り替える</summary>
    public void ToggleLayer(LayerId id, LayerToggle toggle)
    {
        var layer = Scene.GetLayer(id);
        UpdateLayer(toggle switch
        {
            LayerToggle.Visible => layer with { Visible = !layer.Visible },
            LayerToggle.Locked => layer with { Locked = !layer.Locked },
            LayerToggle.Muted => layer with { Muted = !layer.Muted },
            _ => layer with { Solo = !layer.Solo },
        });
    }

    partial void OnPixelsPerFrameChanged(double value) => Invalidate();

    partial void OnScrollXChanged(double value) => Invalidated?.Invoke(this, EventArgs.Empty);

    partial void OnScrollYChanged(double value) => Invalidated?.Invoke(this, EventArgs.Empty);

    partial void OnViewportWidthChanged(double value) => Invalidate();

    partial void OnViewportHeightChanged(double value) => Invalidate();

    partial void OnSnapChanged(bool value) => _session.Settings.Snap = value;

    private bool HasSelection() => !_session.Selection.IsEmpty;

    private long NewItemLength() => (long)Math.Round(Scene.Fps.ToDouble() * NewItemSeconds);

    // アイテムを空いているレイヤーに置いて選ぶ（空きがなければレイヤーを足す）
    private async Task PlaceAsync(Item item, string description, int? preferredLayer = null)
    {
        var project = _session.Project;
        var sceneId = _session.SceneId;
        var preferred = preferredLayer ?? (_session.SelectedItems.FirstOrDefault() is { } selected ? Scene.LayerIndex(selected.Layer) : 0);
        var (layer, addLayer) = MediaItems.FindFreeLayer(project, sceneId, item.Start, item.Length, preferred);
        var operations = new List<IOperation>();
        if (addLayer is not null)
        {
            operations.Add(addLayer);
            project = addLayer.Apply(project).Project;
        }

        var placed = item with { Layer = layer };
        operations.Add(EditCommands.AddItem(project, sceneId, placed));
        if (await RunAsync("置けません", (_, _) => Operations.Combine(description, operations)))
        {
            _session.Selection = [placed.Id];
        }
    }

    private bool CanPaste() => _clipboard is not null;

    // 分割する対象：選んでいるアイテム（なければ、再生位置にかかっているすべてのアイテム）
    private IEnumerable<ItemId> Targets(Project project, SceneId sceneId, long frame) =>
        _session.Selection.IsEmpty ? project.GetScene(sceneId).ItemsAt(frame).Select(item => item.Id) : _session.Selection;

    private async Task MoveLayerAsync(LayerId layer, int delta)
    {
        var index = Scene.LayerIndex(layer) + delta;
        if (index >= 0 && index < Scene.Layers.Count)
        {
            await RunAsync("レイヤーを動かせません", (project, scene) => EditCommands.MoveLayer(project, scene, layer, index));
        }
    }

    private void UpdateLayer(Layer layer) => _session.Execute(EditCommands.UpdateLayer(_session.Project, _session.SceneId, layer));

    private async Task<bool> RunAsync(string title, Func<Project, SceneId, IOperation> build)
    {
        if (_session.Execute(build) is { } error)
        {
            await _dialogs.ShowErrorAsync(title, error);
            return false;
        }

        return true;
    }

    private void Invalidate()
    {
        ScrollX = Math.Clamp(ScrollX, 0, MaxScrollX);
        ScrollY = Math.Clamp(ScrollY, 0, MaxScrollY);
        OnPropertyChanged(nameof(ContentWidth));
        OnPropertyChanged(nameof(MaxScrollX));
        OnPropertyChanged(nameof(MaxScrollY));
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySelectionCommands()
    {
        foreach (var command in new IRelayCommand[]
                 {
                     DeleteCommand, RippleDeleteCommand, CopyCommand, CutCommand, DuplicateCommand, GroupCommand, UngroupCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>図形の種類の選択肢</summary>
public sealed record ShapeChoice(ShapeKind Kind, string Name)
{
    public override string ToString() => Name;
}

/// <summary>レイヤーの切り替え</summary>
public enum LayerToggle
{
    Visible,
    Locked,
    Muted,
    Solo,
}
