using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Controls;

/// <summary>
/// プレビューの絵。描いたフレームを縦横比を保って中央に置き、グリッド・セーフエリア・選んだアイテムの枠を重ねる。
/// Interactive なら、アイテムを押して選び、ドラッグで移動、枠の角で拡大・縮小、上の丸で回転する
/// </summary>
public sealed class PreviewSurface : Control
{
    public static readonly StyledProperty<PreviewViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<PreviewSurface, PreviewViewModel?>(nameof(ViewModel));

    public static readonly StyledProperty<bool> InteractiveProperty =
        AvaloniaProperty.Register<PreviewSurface, bool>(nameof(Interactive), true);

    private const double HandleSize = 8;
    private const double RotateHandleDistance = 24;

    private PreviewViewModel? _attached;
    private bool _dragging;

    static PreviewSurface()
    {
        AffectsRender<PreviewSurface>(ViewModelProperty);
        FocusableProperty.OverrideDefaultValue<PreviewSurface>(true);
    }

    public PreviewViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public bool Interactive
    {
        get => GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    /// <summary>シーンを表示している範囲（コントロールの座標）</summary>
    public Rect SceneRect
    {
        get
        {
            if (ViewModel is not { } viewModel)
            {
                return default;
            }

            var format = viewModel.Format;
            var scale = Math.Min(Bounds.Width / format.Width, Bounds.Height / format.Height);
            var width = format.Width * scale;
            var height = format.Height * scale;
            return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        }
    }

    /// <summary>コントロールの座標 → シーンの座標</summary>
    public Vector2 ToScene(Point point)
    {
        var rect = SceneRect;
        var scale = rect.Width / ViewModel!.Format.Width;
        return new Vector2((float)((point.X - rect.X) / scale), (float)((point.Y - rect.Y) / scale));
    }

    public Point FromScene(Vector2 point)
    {
        var rect = SceneRect;
        var scale = rect.Width / ViewModel!.Format.Width;
        return new Point(rect.X + point.X * scale, rect.Y + point.Y * scale);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (_attached is not null)
            {
                _attached.FrameShown -= OnFrameShown;
            }

            _attached = ViewModel;
            if (_attached is not null)
            {
                _attached.FrameShown += OnFrameShown;
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestTick();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush("PreviewBackgroundColor", Colors.Black), new Rect(Bounds.Size));
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var rect = SceneRect;
        if (viewModel.Bitmap is { } bitmap)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
            }
        }

        var guide = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
        if (viewModel.ShowGrid)
        {
            // 三分割の線
            for (var i = 1; i < 3; i++)
            {
                context.DrawLine(guide, new Point(rect.X + rect.Width * i / 3, rect.Y), new Point(rect.X + rect.Width * i / 3, rect.Bottom));
                context.DrawLine(guide, new Point(rect.X, rect.Y + rect.Height * i / 3), new Point(rect.Right, rect.Y + rect.Height * i / 3));
            }
        }

        if (viewModel.ShowSafeArea)
        {
            // アクションセーフ（93%）とタイトルセーフ（90%）
            var dashed = new Pen(guide.Brush, 1, new DashStyle([4, 4], 0));
            context.DrawRectangle(null, guide, rect.Deflate(new Thickness(rect.Width * 0.035, rect.Height * 0.035)));
            context.DrawRectangle(null, dashed, rect.Deflate(new Thickness(rect.Width * 0.05, rect.Height * 0.05)));
        }

        if (Interactive && viewModel.Session.Selection.Count > 0)
        {
            var selection = new Pen(Brush("SelectionColor", Colors.Gold), 1.5);
            var fill = Brush("PreviewBackgroundColor", Colors.Black);
            foreach (var outline in viewModel.Outlines().Where(outline => viewModel.Session.Selection.Contains(outline.Item)))
            {
                var corners = outline.Corners.Select(FromScene).ToArray();
                context.DrawGeometry(null, selection, new PolylineGeometry([.. corners, corners[0]], false));
                foreach (var corner in corners)
                {
                    context.DrawRectangle(fill, selection, HandleRect(corner));
                }

                var rotate = RotateHandle(corners);
                context.DrawLine(selection, Mid(corners[0], corners[1]), rotate);
                context.DrawEllipse(fill, selection, rotate, HandleSize / 2 + 1, HandleSize / 2 + 1);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Interactive || ViewModel is not { } viewModel || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        viewModel.BeginDrag(ToScene(point), ModeAt(viewModel, point), e.KeyModifiers.HasFlag(KeyModifiers.Control));
        _dragging = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!Interactive || ViewModel is not { } viewModel)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_dragging)
        {
            viewModel.UpdateDrag(ToScene(point), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }

        Cursor = ModeAt(viewModel, point) switch
        {
            PreviewDragMode.Scale => new Cursor(StandardCursorType.SizeAll),
            PreviewDragMode.Rotate => new Cursor(StandardCursorType.Hand),
            _ => Cursor.Default,
        };
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            ViewModel?.EndDrag();
            e.Pointer.Capture(null);
        }
    }

    // 選んでいるアイテムの枠の角なら拡大、上の丸なら回転、それ以外は移動（とアイテムの選択）
    private PreviewDragMode ModeAt(PreviewViewModel viewModel, Point point)
    {
        if (viewModel.Session.Selection.Count == 0)
        {
            return PreviewDragMode.Move;
        }

        foreach (var outline in viewModel.Outlines().Where(outline => viewModel.Session.Selection.Contains(outline.Item)))
        {
            var corners = outline.Corners.Select(FromScene).ToArray();
            if (HandleRect(RotateHandle(corners)).Inflate(3).Contains(point))
            {
                return PreviewDragMode.Rotate;
            }

            if (corners.Any(corner => HandleRect(corner).Inflate(3).Contains(point)))
            {
                return PreviewDragMode.Scale;
            }
        }

        return PreviewDragMode.Move;
    }

    private static Rect HandleRect(Point center) => new(center.X - HandleSize / 2, center.Y - HandleSize / 2, HandleSize, HandleSize);

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    // 上の辺の中央から、外向きに少し離れた位置
    private static Point RotateHandle(Point[] corners)
    {
        var top = Mid(corners[0], corners[1]);
        var bottom = Mid(corners[3], corners[2]);
        var direction = top - bottom;
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        return length < 1 ? top - new Point(0, RotateHandleDistance) : top + direction / length * RotateHandleDistance;
    }

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? new SolidColorBrush(color) : new SolidColorBrush(fallback);

    private void OnFrameShown(object? sender, EventArgs e) => InvalidateVisual();

    // 画面の更新に合わせて、表示するフレームを進める
    private void RequestTick()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        top.RequestAnimationFrame(_ =>
        {
            if (TopLevel.GetTopLevel(this) is null)
            {
                return;
            }

            if (Interactive)
            {
                ViewModel?.Tick();
            }

            RequestTick();
        });
    }
}
