using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NakkuntMovieMaker.Audio;

namespace NakkuntMovieMaker.App.Controls;

/// <summary>
/// 音量レベルメーター（左右の縦の棒）。RMS を棒で、ピークを細い線で描き、ピークは 1.5 秒保ってから下げる。
/// 目盛りは -60〜0 dBFS で、-12 dB から黄、-3 dB から赤にする。0 dB を超えたら上の印を赤くする（クリップ）
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<StereoLevel> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, StereoLevel>(nameof(Level), StereoLevel.Silent);

    public static readonly StyledProperty<bool> ShowScaleProperty =
        AvaloniaProperty.Register<LevelMeter, bool>(nameof(ShowScale), true);

    public const double MinDb = -60;
    private const double HoldSeconds = 1.5;
    private const double FallDbPerSecond = 20;
    private const double ScaleWidth = 22;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _hold = [MinDb, MinDb];
    private readonly double[] _holdTime = [0, 0];
    private readonly bool[] _clipped = [false, false];
    private double _lastUpdate;

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty, ShowScaleProperty);
    }

    public StereoLevel Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool ShowScale
    {
        get => GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>dB を 0（下）〜 1（上）の位置にする</summary>
    public static double Position(double db) => double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db - MinDb) / -MinDb, 0, 1);

    /// <summary>クリップの印を消す（押したとき）</summary>
    public void ResetClip()
    {
        _clipped[0] = _clipped[1] = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ResetClip();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty)
        {
            UpdateHold();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var scale = ShowScale ? ScaleWidth : 0;
        var barsWidth = Math.Max(0, bounds.Width - scale);
        var barWidth = Math.Max(1, (barsWidth - 2) / 2);
        var top = 6.0;
        var height = Math.Max(1, bounds.Height - top);
        context.FillRectangle(Brush("MeterBackgroundColor"), new Rect(0, 0, barsWidth, bounds.Height));

        var green = Brush("MeterGreenColor");
        var yellow = Brush("MeterYellowColor");
        var red = Brush("MeterRedColor");
        AudioLevel[] levels = [Level.Left, Level.Right];
        for (var channel = 0; channel < 2; channel++)
        {
            var x = channel * (barWidth + 2);
            var rms = Position(levels[channel].RmsDb);
            // 色の境目ごとに塗る
            Fill(context, x, barWidth, top, height, 0, Math.Min(rms, Position(-12)), green);
            Fill(context, x, barWidth, top, height, Position(-12), Math.Min(rms, Position(-3)), yellow);
            Fill(context, x, barWidth, top, height, Position(-3), rms, red);

            var hold = Position(_hold[channel]);
            if (hold > 0)
            {
                var y = top + height * (1 - hold);
                context.FillRectangle(_hold[channel] >= -3 ? red : _hold[channel] >= -12 ? yellow : green, new Rect(x, y - 1, barWidth, 2));
            }

            context.FillRectangle(_clipped[channel] ? red : Brush("TimelineGridColor"), new Rect(x, 0, barWidth, 4));
        }

        if (ShowScale)
        {
            var text = Brush("SecondaryTextColor");
            foreach (var db in new[] { 0, -6, -12, -24, -36, -48 })
            {
                var y = top + height * (1 - Position(db));
                var label = new FormattedText(db.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, text);
                context.DrawText(label, new Point(barsWidth + 3, Math.Clamp(y - label.Height / 2, 0, bounds.Height - label.Height)));
            }
        }
    }

    private static void Fill(DrawingContext context, double x, double width, double top, double height, double from, double to, IBrush brush)
    {
        if (to > from)
        {
            context.FillRectangle(brush, new Rect(x, top + height * (1 - to), width, height * (to - from)));
        }
    }

    // ピークを保ち、時間が経ったら下げる
    private void UpdateHold()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = now - _lastUpdate;
        _lastUpdate = now;
        AudioLevel[] levels = [Level.Left, Level.Right];
        for (var channel = 0; channel < 2; channel++)
        {
            var peak = levels[channel].PeakDb;
            if (peak >= 0)
            {
                _clipped[channel] = true;
            }

            if (peak >= _hold[channel])
            {
                _hold[channel] = peak;
                _holdTime[channel] = now;
            }
            else if (now - _holdTime[channel] > HoldSeconds)
            {
                _hold[channel] = Math.Max(MinDb, _hold[channel] - FallDbPerSecond * elapsed);
            }
        }
    }

    private IBrush Brush(string key) =>
        new SolidColorBrush(this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Colors.Gray);
}
