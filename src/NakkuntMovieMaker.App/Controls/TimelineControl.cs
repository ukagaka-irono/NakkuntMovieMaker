using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.App.Controls;

/// <summary>
/// タイムラインを描き、マウスで編集する。上に定規（押すと再生位置を動かす）、左にレイヤーの見出し（表示・ロック・ミュート・ソロ）。
/// アイテムは、押して選び（Ctrl で追加・解除）、ドラッグで移動（上下でレイヤーも）、端のドラッグで長さを変える
/// （Shift で後ろを詰める・押し出す、Alt で隣のアイテムとの境目を動かす）。何もないところのドラッグで範囲を選ぶ。
/// Ctrl + ホイールでズーム、Shift + ホイールで横にスクロールする
/// </summary>
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<TimelineViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<TimelineControl, TimelineViewModel?>(nameof(ViewModel));

    private const double EdgeWidth = 6;
    private const double ToggleSize = 18;
    private static readonly string[] ToggleLabels = ["V", "L", "M", "S"];
    private static readonly string[] ToggleTips = ["表示", "ロック", "ミュート", "ソロ"];

    private TimelineViewModel? _attached;
    private DragKind _drag;
    private Point _pressPoint;
    private long _pressFrame;
    private int _pressLayer;
    private Item? _pressItem;
    private Item? _rollPartner;
    private Point _currentPoint;
    private ImmutableHashSet<ItemId> _selectionAtPress = [];
    private bool _moved;

    static TimelineControl()
    {
        FocusableProperty.OverrideDefaultValue<TimelineControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<TimelineControl>(true);
    }

    public TimelineControl()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private enum DragKind
    {
        None,
        Seek,
        Move,
        TrimStart,
        TrimEnd,
        RippleTrimEnd,
        Roll,
        RubberBand,
    }

    public TimelineViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (_attached is not null)
            {
                _attached.Invalidated -= OnInvalidated;
            }

            _attached = ViewModel;
            if (_attached is not null)
            {
                _attached.Invalidated += OnInvalidated;
                UpdateViewport();
            }

            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateViewport();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brush("TimelineBackgroundColor"), new Rect(size));
        if (ViewModel is not { } vm)
        {
            return;
        }

        var scene = vm.Scene;
        var trackArea = new Rect(TimelineViewModel.HeaderWidth, TimelineViewModel.RulerHeight, Math.Max(0, size.Width - TimelineViewModel.HeaderWidth), Math.Max(0, size.Height - TimelineViewModel.RulerHeight));
        var text = Brush("TimelineTextColor");
        var secondary = Brush("SecondaryTextColor");

        // レイヤーの行
        using (context.PushClip(new Rect(0, TimelineViewModel.RulerHeight, size.Width, trackArea.Height)))
        {
            for (var index = 0; index < scene.Layers.Count; index++)
            {
                var y = vm.LayerToY(index);
                if (y + TimelineViewModel.LayerHeight < TimelineViewModel.RulerHeight || y > size.Height)
                {
                    continue;
                }

                var row = new Rect(0, y, size.Width, TimelineViewModel.LayerHeight);
                if (index % 2 == 1)
                {
                    context.FillRectangle(Brush("TimelineLayerAltColor"), row);
                }

                context.DrawLine(new Pen(Brush("TimelineGridColor"), 1), row.BottomLeft, row.BottomRight);
            }

            // 範囲
            if (vm.Session.RangeStart is not null || vm.Session.RangeEnd is not null)
            {
                var start = vm.FrameToX(vm.Session.RangeStart ?? 0);
                var end = vm.FrameToX(vm.Session.RangeEnd ?? scene.Duration);
                context.FillRectangle(Brush("RangeColor"), new Rect(start, TimelineViewModel.RulerHeight, Math.Max(0, end - start), trackArea.Height));
            }

            using (context.PushClip(trackArea))
            {
                DrawItems(context, vm, trackArea);
            }

            DrawHeaders(context, vm, text, secondary);
        }

        DrawRuler(context, vm, size, text, secondary);

        // スナップした位置と再生位置
        if (vm.SnapIndicator is { } snap)
        {
            var x = vm.FrameToX(snap);
            context.DrawLine(new Pen(Brush("SnapColor"), 1, new DashStyle([3, 3], 0)), new Point(x, 0), new Point(x, size.Height));
        }

        var playhead = vm.FrameToX(vm.Session.CurrentFrame);
        if (playhead >= TimelineViewModel.HeaderWidth)
        {
            var pen = new Pen(Brush("PlayheadColor"), 1.5);
            context.DrawLine(pen, new Point(playhead, 0), new Point(playhead, size.Height));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(playhead - 6, 0), true);
                g.LineTo(new Point(playhead + 6, 0));
                g.LineTo(new Point(playhead, 8));
                g.EndFigure(true);
            }

            context.DrawGeometry(pen.Brush, null, head);
        }

        if (_drag == DragKind.RubberBand && _moved)
        {
            var rect = new Rect(_pressPoint, _currentPoint).Normalize();
            context.DrawRectangle(new SolidColorBrush(Colors.White, 0.08), new Pen(Brush("SelectionColor"), 1), rect);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel is not { } vm)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        _pressPoint = point;
        _currentPoint = point;
        _moved = false;
        _pressFrame = vm.XToFrame(point.X);
        _pressLayer = vm.YToLayer(point.Y);
        _selectionAtPress = vm.Session.Selection;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // 定規：再生位置を動かす
        if (point.Y < TimelineViewModel.RulerHeight)
        {
            if (point.X >= TimelineViewModel.HeaderWidth && properties.IsLeftButtonPressed)
            {
                vm.Session.Seek(_pressFrame);
                StartDrag(DragKind.Seek, e);
            }
            else if (properties.IsRightButtonPressed && MarkerAt(vm, point) is { } marker)
            {
                ShowMarkerMenu(vm, marker);
            }

            return;
        }

        // レイヤーの見出し
        if (point.X < TimelineViewModel.HeaderWidth)
        {
            if (_pressLayer >= 0 && _pressLayer < vm.Scene.Layers.Count)
            {
                var layer = vm.Scene.Layers[_pressLayer];
                if (properties.IsRightButtonPressed)
                {
                    ShowLayerMenu(vm, layer, _pressLayer);
                }
                else if (ToggleAt(vm, point) is { } toggle)
                {
                    vm.ToggleLayer(layer.Id, toggle);
                }
                else if (e.ClickCount == 2)
                {
                    vm.RenameLayerCommand.Execute(layer.Id);
                }
            }

            return;
        }

        var item = ItemAt(vm, point);
        if (properties.IsRightButtonPressed)
        {
            if (item is not null && !vm.Session.Selection.Contains(item.Id))
            {
                vm.Session.Selection = [item.Id];
            }

            ShowItemMenu(vm);
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (item is null)
        {
            if (!control)
            {
                vm.Session.Selection = [];
            }

            StartDrag(DragKind.RubberBand, e);
            return;
        }

        _pressItem = item;
        if (control)
        {
            vm.Session.Selection = vm.Session.Selection.Contains(item.Id) ? vm.Session.Selection.Remove(item.Id) : vm.Session.Selection.Add(item.Id);
            return;
        }

        if (!vm.Session.Selection.Contains(item.Id))
        {
            vm.Session.Selection = [item.Id];
        }

        var left = vm.FrameToX(item.Start);
        var right = vm.FrameToX(item.End);
        if (point.X - left <= EdgeWidth && right - left > EdgeWidth * 3)
        {
            StartDrag(DragKind.TrimStart, e);
        }
        else if (right - point.X <= EdgeWidth)
        {
            _rollPartner = vm.Scene.ItemsOnLayer(item.Layer).FirstOrDefault(other => other.Start == item.End);
            StartDrag(
                e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _rollPartner is not null ? DragKind.Roll
                : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragKind.RippleTrimEnd
                : DragKind.TrimEnd,
                e);
        }
        else
        {
            StartDrag(DragKind.Move, e);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ViewModel is not { } vm)
        {
            return;
        }

        var point = e.GetPosition(this);
        _currentPoint = point;
        if (_drag == DragKind.None)
        {
            UpdateCursor(vm, point);
            return;
        }

        if (!_moved && Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) < 3)
        {
            return;
        }

        _moved = true;
        var frame = vm.XToFrame(point.X);
        var exclude = vm.Session.Selection;
        switch (_drag)
        {
            case DragKind.Seek:
                vm.Session.Seek(frame);
                break;
            case DragKind.Move when _pressItem is { } item:
                var delta = frame - _pressFrame;
                // つかんだアイテムの先頭か終わりを、近くの端に合わせる
                var snappedStart = vm.SnapFrame(item.Start + delta, exclude);
                if (vm.SnapIndicator is null)
                {
                    var snappedEnd = vm.SnapFrame(item.End + delta, exclude);
                    delta = snappedEnd - item.End;
                }
                else
                {
                    delta = snappedStart - item.Start;
                }

                var layerDelta = vm.YToLayer(point.Y) - _pressLayer;
                vm.UpdatePreviewEdit((project, scene) => EditCommands.MoveItems(project, scene, exclude, delta, layerDelta));
                break;
            case DragKind.TrimStart when _pressItem is { } item:
                var start = vm.SnapFrame(frame, exclude);
                vm.UpdatePreviewEdit((project, scene) => EditCommands.TrimStart(project, scene, item.Id, Math.Min(start, item.End - 1)));
                break;
            case DragKind.TrimEnd when _pressItem is { } item:
                var end = vm.SnapFrame(frame, exclude);
                vm.UpdatePreviewEdit((project, scene) => EditCommands.TrimEnd(project, scene, item.Id, Math.Max(end, item.Start + 1)));
                break;
            case DragKind.RippleTrimEnd when _pressItem is { } item:
                var rippleEnd = vm.SnapFrame(frame, exclude);
                vm.UpdatePreviewEdit((project, scene) => EditCommands.RippleTrimEnd(project, scene, item.Id, Math.Max(rippleEnd, item.Start + 1)));
                break;
            case DragKind.Roll when _pressItem is { } item && _rollPartner is { } partner:
                var boundary = Math.Clamp(vm.SnapFrame(frame, exclude.Add(partner.Id)), item.Start + 1, partner.End - 1);
                vm.UpdatePreviewEdit((project, scene) => EditCommands.RollEdit(project, scene, item.Id, partner.Id, boundary));
                break;
            case DragKind.RubberBand:
                var rect = new Rect(_pressPoint, point).Normalize();
                var hits = vm.Scene.Items.Values.Where(candidate => ItemRect(vm, candidate) is { } r && r.Intersects(rect)).Select(candidate => candidate.Id);
                vm.Session.Selection = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? _selectionAtPress.Union(hits) : [.. hits];
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragKind.None || ViewModel is not { } vm)
        {
            return;
        }

        // 何もないところをクリックしただけなら、再生位置を動かす
        if (_drag == DragKind.RubberBand && !_moved)
        {
            vm.Session.Seek(_pressFrame);
        }

        if (_drag is not (DragKind.Seek or DragKind.RubberBand))
        {
            vm.EndPreviewEdit();
        }

        _drag = DragKind.None;
        _pressItem = null;
        _rollPartner = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ViewModel is not { } vm)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ZoomAt(e.Delta.Y > 0 ? 1.25 : 0.8, Math.Max(point.X, TimelineViewModel.HeaderWidth));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0 || vm.MaxScrollY <= 0)
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            vm.ScrollX = Math.Clamp(vm.ScrollX - delta * 80, 0, vm.MaxScrollX);
        }
        else
        {
            vm.ScrollY = Math.Clamp(vm.ScrollY - e.Delta.Y * TimelineViewModel.LayerHeight, 0, vm.MaxScrollY);
        }

        e.Handled = true;
    }

    private void StartDrag(DragKind kind, PointerPressedEventArgs e)
    {
        _drag = kind;
        if (kind is not (DragKind.Seek or DragKind.RubberBand))
        {
            ViewModel?.BeginPreviewEdit();
        }

        e.Pointer.Capture(this);
    }

    private void UpdateCursor(TimelineViewModel vm, Point point)
    {
        string? tip = null;
        var cursor = Cursor.Default;
        if (point.X < TimelineViewModel.HeaderWidth && ToggleAt(vm, point) is { } toggle)
        {
            tip = ToggleTips[(int)toggle];
            cursor = new Cursor(StandardCursorType.Hand);
        }
        else if (point.Y >= TimelineViewModel.RulerHeight && ItemAt(vm, point) is { } item)
        {
            var left = vm.FrameToX(item.Start);
            var right = vm.FrameToX(item.End);
            cursor = point.X - left <= EdgeWidth || right - point.X <= EdgeWidth ? new Cursor(StandardCursorType.SizeWestEast) : new Cursor(StandardCursorType.Hand);
            tip = vm.Label(item);
        }

        Cursor = cursor;
        ToolTip.SetTip(this, tip);
    }

    // OS からのファイルと、素材の一覧からの素材を受け付ける
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(MediaItems.AssetFormat) || e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        if (ViewModel is { } vm)
        {
            vm.SnapFrame(vm.XToFrame(e.GetPosition(this).X));
            InvalidateVisual();
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var point = e.GetPosition(this);
        var frame = vm.XToFrame(point.X);
        var layer = Math.Max(0, vm.YToLayer(point.Y));
        if (e.DataTransfer.TryGetValue(MediaItems.AssetFormat) is { } id && Guid.TryParse(id, out var asset))
        {
            await vm.DropAssetAsync(new AssetId(asset), frame, layer);
        }
        else if (e.DataTransfer.TryGetFiles() is { } files)
        {
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToList();
            await vm.DropFilesAsync(paths, vm.SnapFrame(frame), layer);
        }

        vm.EndPreviewEdit();
    }

    private void DrawItems(DrawingContext context, TimelineViewModel vm, Rect area)
    {
        var scene = vm.Scene;
        var firstFrame = vm.XToFrame(area.Left) - 1;
        var lastFrame = vm.XToFrame(area.Right) + 1;
        var selection = new Pen(Brush("SelectionColor"), 2);
        var border = new Pen(new SolidColorBrush(Colors.Black, 0.35), 1);
        var label = Brush("TimelineTextColor");
        foreach (var item in scene.Items.Values)
        {
            if (item.End < firstFrame || item.Start > lastFrame || ItemRect(vm, item) is not { } rect)
            {
                continue;
            }

            var layer = scene.FindLayer(item.Layer);
            var color = ItemColor(item);
            // 非表示・ロックのレイヤー、ミュートしたレイヤーの音は薄く描く
            var faded = layer is { Visible: false } or { Locked: true } || (layer is { Muted: true } && item is AudioItem);
            var dim = faded ? 0.45 : 1;
            context.FillRectangle(new SolidColorBrush(color, dim), rect, 3);
            using (context.PushClip(rect))
            {
                var textLeft = rect.X + 4;
                var path = item switch
                {
                    VideoItem video => vm.AssetPath(video.Asset),
                    ImageItem image => vm.AssetPath(image.Asset),
                    _ => null,
                };
                if (path is not null && rect.Width > 48 && vm.GetThumbnail(path) is { } thumbnail)
                {
                    var height = rect.Height - 2;
                    var width = height * thumbnail.Size.Width / thumbnail.Size.Height;
                    context.DrawImage(thumbnail, new Rect(rect.X + 1, rect.Y + 1, width, height));
                    textLeft += width + 2;
                }

                if (vm.AudioPath(item) is { } audio && rect.Width > 4)
                {
                    DrawWaveform(context, vm, item, audio, rect);
                }

                var title = new FormattedText(vm.Label(item), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 11, label);
                context.DrawText(title, new Point(Math.Max(textLeft, area.Left + 4), rect.Y + 3));

                // トランジション（先頭の三角）とグループ（右上の点）
                if (item.Transition is not null)
                {
                    var triangle = new StreamGeometry();
                    using (var g = triangle.Open())
                    {
                        g.BeginFigure(rect.TopLeft, true);
                        g.LineTo(rect.TopLeft + new Point(12, 0));
                        g.LineTo(rect.BottomLeft);
                        g.EndFigure(true);
                    }

                    context.DrawGeometry(new SolidColorBrush(Colors.White, 0.35), null, triangle);
                }

                if (item.Group is not null)
                {
                    context.DrawEllipse(Brushes.White, null, new Point(rect.Right - 6, rect.Y + 6), 2.5, 2.5);
                }
            }

            context.DrawRectangle(null, vm.Session.Selection.Contains(item.Id) ? selection : border, rect, 3, 3);
        }
    }

    // 音の波形（素材の使っている範囲を、横 1 ピクセルずつ最小と最大で描く）
    private void DrawWaveform(DrawingContext context, TimelineViewModel vm, Item item, string path, Rect rect)
    {
        if (vm.GetPeaks(path) is not { } peaks)
        {
            return;
        }

        var playback = item switch
        {
            VideoItem video => video.Playback,
            AudioItem audio => audio.Playback,
            _ => MediaPlayback.Normal,
        };
        var fps = vm.Scene.Fps.ToDouble();
        var speed = playback.Speed.ToDouble();
        var offset = playback.SourceOffset.ToDouble();
        var middle = rect.Y + rect.Height * 0.62;
        var amplitude = rect.Height * 0.36;
        var pen = new Pen(Brush("WaveformColor"), 1);
        var left = Math.Max(rect.Left, TimelineViewModel.HeaderWidth);
        var right = Math.Min(rect.Right, Bounds.Width);
        for (var x = Math.Floor(left); x < right; x++)
        {
            var localStart = (x - rect.X) / vm.PixelsPerFrame;
            var localEnd = (x + 1 - rect.X) / vm.PixelsPerFrame;
            var from = (long)((offset + localStart / fps * speed) * peaks.SampleRate);
            var to = (long)((offset + localEnd / fps * speed) * peaks.SampleRate);
            if (playback.Reverse)
            {
                var length = item.Length / fps * speed * peaks.SampleRate;
                (from, to) = ((long)(offset * peaks.SampleRate + length) - to, (long)(offset * peaks.SampleRate + length) - from);
            }

            var (min, max) = peaks.GetRange(0, Math.Max(0, from), Math.Max(from + 1, to));
            context.DrawLine(pen, new Point(x + 0.5, middle - max * amplitude), new Point(x + 0.5, middle - min * amplitude));
        }
    }

    private void DrawHeaders(DrawingContext context, TimelineViewModel vm, IBrush text, IBrush secondary)
    {
        var scene = vm.Scene;
        var background = Brush("PanelBackgroundColor");
        context.FillRectangle(background, new Rect(0, TimelineViewModel.RulerHeight, TimelineViewModel.HeaderWidth, Bounds.Height));
        var on = Brush("SelectionColor");
        var grid = new Pen(Brush("TimelineGridColor"), 1);
        for (var index = 0; index < scene.Layers.Count; index++)
        {
            var y = vm.LayerToY(index);
            if (y + TimelineViewModel.LayerHeight < TimelineViewModel.RulerHeight || y > Bounds.Height)
            {
                continue;
            }

            var layer = scene.Layers[index];
            var name = new FormattedText($"{index + 1}  {layer.Name}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, text)
            {
                MaxTextWidth = TimelineViewModel.HeaderWidth - 4 * (ToggleSize + 2) - 8,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(name, new Point(6, y + (TimelineViewModel.LayerHeight - name.Height) / 2));
            bool[] states = [layer.Visible, layer.Locked, layer.Muted, layer.Solo];
            for (var i = 0; i < 4; i++)
            {
                var box = ToggleRect(vm, index, (LayerToggle)i);
                // 表示はオンが普通なので、オフのときに目立たせる
                var active = i == 0 ? !states[i] : states[i];
                context.DrawRectangle(active ? on : null, grid, box, 3, 3);
                var glyph = new FormattedText(ToggleLabels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, active ? Brushes.Black : secondary);
                context.DrawText(glyph, new Point(box.X + (box.Width - glyph.Width) / 2, box.Y + (box.Height - glyph.Height) / 2));
            }

            context.DrawLine(grid, new Point(0, y + TimelineViewModel.LayerHeight), new Point(TimelineViewModel.HeaderWidth, y + TimelineViewModel.LayerHeight));
        }

        context.DrawLine(grid, new Point(TimelineViewModel.HeaderWidth, 0), new Point(TimelineViewModel.HeaderWidth, Bounds.Height));
    }

    private void DrawRuler(DrawingContext context, TimelineViewModel vm, Size size, IBrush text, IBrush secondary)
    {
        context.FillRectangle(Brush("TimelineRulerColor"), new Rect(0, 0, size.Width, TimelineViewModel.RulerHeight));
        var fps = vm.Scene.Fps;
        var rate = fps.ToDouble();
        var step = RulerStep(vm.PixelsPerFrame, rate);
        var minor = Math.Max(1, step / 5);
        var first = vm.XToFrame(TimelineViewModel.HeaderWidth) / minor * minor;
        var last = vm.XToFrame(size.Width) + minor;
        var tick = new Pen(secondary, 1);
        using (context.PushClip(new Rect(TimelineViewModel.HeaderWidth, 0, Math.Max(0, size.Width - TimelineViewModel.HeaderWidth), TimelineViewModel.RulerHeight)))
        {
            for (var frame = first; frame <= last; frame += minor)
            {
                var x = vm.FrameToX(frame);
                var major = frame % step == 0;
                context.DrawLine(tick, new Point(x, TimelineViewModel.RulerHeight - (major ? 10 : 4)), new Point(x, TimelineViewModel.RulerHeight));
                if (major)
                {
                    var label = new FormattedText(RulerLabel(frame, fps, step < rate), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, text);
                    context.DrawText(label, new Point(x + 3, 3));
                }
            }

            foreach (var marker in vm.Scene.Markers)
            {
                var x = vm.FrameToX(marker.Frame);
                var color = new SolidColorBrush(Color.FromArgb(marker.Color.A, marker.Color.R, marker.Color.G, marker.Color.B));
                context.DrawLine(new Pen(color, 1.5), new Point(x, 0), new Point(x, TimelineViewModel.RulerHeight));
                var name = new FormattedText(marker.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 10, color);
                context.DrawText(name, new Point(x + 3, TimelineViewModel.RulerHeight - name.Height - 1));
            }
        }

        context.FillRectangle(Brush("PanelBackgroundColor"), new Rect(0, 0, TimelineViewModel.HeaderWidth, TimelineViewModel.RulerHeight));
        var timecode = new FormattedText(PreviewViewModel.FormatTimecode(vm.Session.CurrentFrame, fps), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, text);
        context.DrawText(timecode, new Point(8, (TimelineViewModel.RulerHeight - timecode.Height) / 2));
    }

    // 目盛りの間隔（フレーム）。ラベルどうしが 80 ピクセル以上離れる、きりのよい間隔
    private static long RulerStep(double pixelsPerFrame, double fps)
    {
        var second = Math.Max(1, (long)Math.Round(fps));
        long[] candidates = [1, 2, 5, 10, second / 2, second, second * 2, second * 5, second * 10, second * 30, second * 60, second * 300, second * 600, second * 1800, second * 3600];
        return candidates.Where(step => step > 0).FirstOrDefault(step => step * pixelsPerFrame >= 80, second * 3600);
    }

    private static string RulerLabel(long frame, Rational fps, bool showFrames)
    {
        var seconds = (long)Math.Floor(TimeMath.FrameToSeconds(frame, fps).ToDouble());
        var clock = seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
        return showFrames ? $"{clock}.{frame - TimeMath.SecondsToFrame(seconds, fps, Rounding.Up):00}" : clock;
    }

    private static Rect? ItemRect(TimelineViewModel vm, Item item)
    {
        var index = vm.Scene.LayerIndex(item.Layer);
        if (index < 0)
        {
            return null;
        }

        var left = vm.FrameToX(item.Start);
        var right = vm.FrameToX(item.End);
        return new Rect(left, vm.LayerToY(index) + 3, Math.Max(2, right - left), TimelineViewModel.LayerHeight - 6);
    }

    private static Item? ItemAt(TimelineViewModel vm, Point point)
    {
        if (point.X < TimelineViewModel.HeaderWidth || point.Y < TimelineViewModel.RulerHeight)
        {
            return null;
        }

        var index = vm.YToLayer(point.Y);
        if (index < 0 || index >= vm.Scene.Layers.Count)
        {
            return null;
        }

        var layer = vm.Scene.Layers[index].Id;
        return vm.Scene.ItemsOnLayer(layer).LastOrDefault(item => ItemRect(vm, item) is { } rect && rect.Inflate(new Thickness(2, 0)).Contains(point));
    }

    private static Rect ToggleRect(TimelineViewModel vm, int layerIndex, LayerToggle toggle)
    {
        var y = vm.LayerToY(layerIndex) + (TimelineViewModel.LayerHeight - ToggleSize) / 2;
        var x = TimelineViewModel.HeaderWidth - 4 - (4 - (int)toggle) * (ToggleSize + 2);
        return new Rect(x, y, ToggleSize, ToggleSize);
    }

    private static LayerToggle? ToggleAt(TimelineViewModel vm, Point point)
    {
        var index = vm.YToLayer(point.Y);
        if (index < 0 || index >= vm.Scene.Layers.Count)
        {
            return null;
        }

        foreach (var toggle in Enum.GetValues<LayerToggle>())
        {
            if (ToggleRect(vm, index, toggle).Contains(point))
            {
                return toggle;
            }
        }

        return null;
    }

    private static Marker? MarkerAt(TimelineViewModel vm, Point point) =>
        vm.Scene.Markers.FirstOrDefault(marker => Math.Abs(vm.FrameToX(marker.Frame) - point.X) <= 4);

    private void ShowItemMenu(TimelineViewModel vm) => OpenMenu(
    [
        Item("分割", vm.SplitCommand, "edit.split"),
        new Separator(),
        Item("切り取り", vm.CutCommand, "edit.cut"),
        Item("コピー", vm.CopyCommand, "edit.copy"),
        Item("貼り付け", vm.PasteCommand, "edit.paste"),
        Item("複製", vm.DuplicateCommand, "edit.duplicate"),
        new Separator(),
        Item("削除", vm.DeleteCommand, "edit.delete"),
        Item("削除して詰める", vm.RippleDeleteCommand, "edit.rippleDelete"),
        new Separator(),
        Item("グループ化", vm.GroupCommand, "edit.group"),
        Item("グループ解除", vm.UngroupCommand, "edit.ungroup"),
    ]);

    private void ShowLayerMenu(TimelineViewModel vm, Layer layer, int index) => OpenMenu(
    [
        Item("名前の変更…", vm.RenameLayerCommand, null, layer.Id),
        new Separator(),
        Item("上にレイヤーを追加", vm.AddLayerCommand, null, index),
        Item("下にレイヤーを追加", vm.AddLayerCommand, null, index + 1),
        Item("上へ移動", vm.MoveLayerUpCommand, null, layer.Id),
        Item("下へ移動", vm.MoveLayerDownCommand, null, layer.Id),
        new Separator(),
        Item("レイヤーを削除", vm.RemoveLayerCommand, null, layer.Id),
    ]);

    private void ShowMarkerMenu(TimelineViewModel vm, Marker marker) => OpenMenu(
    [
        Item("名前の変更…", vm.RenameMarkerCommand, null, marker),
        Item("マーカーを削除", vm.RemoveMarkerCommand, null, marker),
    ]);

    private MenuItem Item(string header, System.Windows.Input.ICommand command, string? shortcut, object? parameter = null) => new()
    {
        Header = header,
        Command = command,
        CommandParameter = parameter,
        InputGesture = shortcut is null ? null : (TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel)?.Shortcuts.GestureOf(shortcut),
    };

    private void OpenMenu(IReadOnlyList<Control> items)
    {
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(this);
    }

    private Color ItemColor(Item item) => ResourceColor(item switch
    {
        VideoItem => "ItemVideoColor",
        ImageItem => "ItemImageColor",
        AudioItem => "ItemAudioColor",
        TextItem => "ItemTextColor",
        ShapeItem => "ItemShapeColor",
        VoiceItem => "ItemVoiceColor",
        TachieItem => "ItemTachieColor",
        _ => "TimelineGridColor",
    });

    private Color ResourceColor(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Colors.Gray;

    private IBrush Brush(string key) => new SolidColorBrush(ResourceColor(key));

    private void UpdateViewport()
    {
        if (ViewModel is { } vm)
        {
            vm.ViewportWidth = Math.Max(1, Bounds.Width - TimelineViewModel.HeaderWidth);
            vm.ViewportHeight = Math.Max(1, Bounds.Height - TimelineViewModel.RulerHeight);
        }
    }

    private void OnInvalidated(object? sender, EventArgs e) => InvalidateVisual();
}
