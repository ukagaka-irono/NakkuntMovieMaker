using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>プロパティの入力欄が、値の変更を伝える先（アイテムをどう変えるか、操作の名前）</summary>
public delegate void ApplyChange(Func<Item, Item> change, string description);

/// <summary>プロパティの入力欄。Refresh で今のアイテムの値を読み直す（読み直している間は、変更を伝えない）</summary>
public abstract partial class PropertyEditor(string label, ApplyChange apply) : ObservableObject
{
    private bool _refreshing;

    public string Label { get; } = label;

    /// <summary>アイテムと、アイテムの先頭からの再生位置で、表示する値を読み直す</summary>
    public void Refresh(Item item, long localFrame)
    {
        _refreshing = true;
        try
        {
            Load(item, localFrame);
        }
        finally
        {
            _refreshing = false;
        }
    }

    protected abstract void Load(Item item, long localFrame);

    protected void Apply(Func<Item, Item> change)
    {
        if (!_refreshing)
        {
            apply(change, $"{Label}の変更");
        }
    }
}

/// <summary>
/// キーフレームでアニメーションできる数値。キーフレームがあれば、今の位置にキーフレームを置く（あれば値を変える）。
/// ◆ のボタンで、今の位置のキーフレームを足す・外す
/// </summary>
public sealed partial class AnimatedNumberEditor : PropertyEditor
{
    private readonly AnimatedNumberProperty _definition;
    private Animatable<double> _current = 0.0;
    private long _frame;

    public AnimatedNumberEditor(AnimatedNumberProperty definition, ApplyChange apply)
        : base(definition.Label, apply)
    {
        _definition = definition;
    }

    public decimal Minimum => NumberRange.ToDecimal(_definition.Min);

    public decimal Maximum => NumberRange.ToDecimal(_definition.Max);

    public decimal Increment => (decimal)_definition.Increment;

    public string Format => _definition.Format;

    public static IReadOnlyList<EasingChoice> Easings { get; } = EasingChoice.All;

    [ObservableProperty]
    public partial decimal? Value { get; set; }

    /// <summary>キーフレームがある</summary>
    [ObservableProperty]
    public partial bool IsAnimated { get; set; }

    /// <summary>今の位置にキーフレームがある</summary>
    [ObservableProperty]
    public partial bool HasKey { get; set; }

    /// <summary>今の位置のキーフレームから次への変化のしかた</summary>
    [ObservableProperty]
    public partial EasingChoice? Easing { get; set; }

    protected override void Load(Item item, long localFrame)
    {
        _current = _definition.Get(item);
        _frame = localFrame;
        Value = NumberRange.ToDecimal(Math.Round(_current.Evaluate(localFrame), 6));
        IsAnimated = _current.IsAnimated;
        var key = _current.Keys.FirstOrDefault(candidate => candidate.Frame == localFrame);
        HasKey = _current.IsAnimated && _current.Keys.Any(candidate => candidate.Frame == localFrame);
        Easing = HasKey ? EasingChoice.All.FirstOrDefault(choice => choice.Easing == key.Easing) ?? EasingChoice.All[0] : null;
    }

    [RelayCommand]
    private void ToggleKey()
    {
        var frame = _frame;
        var value = _current.Evaluate(frame);
        Apply(item =>
        {
            var current = _definition.Get(item);
            Animatable<double> next;
            if (!current.IsAnimated)
            {
                next = Animatable<double>.FromKeys([new Keyframe<double>(frame, value)]);
            }
            else if (current.Keys.Any(key => key.Frame == frame))
            {
                next = current.Keys.Length == 1 ? Animatable<double>.Constant(value) : current.RemoveKey(frame);
            }
            else
            {
                next = current.SetKey(new Keyframe<double>(frame, value));
            }

            return _definition.Set(item, next);
        });
    }

    partial void OnValueChanged(decimal? value)
    {
        if (value is not { } entered)
        {
            return;
        }

        var frame = _frame;
        var number = Math.Clamp((double)entered, _definition.Min, _definition.Max);
        Apply(item =>
        {
            var current = _definition.Get(item);
            if (!current.IsAnimated)
            {
                return _definition.Set(item, Animatable<double>.Constant(number));
            }

            var easing = current.Keys.FirstOrDefault(key => key.Frame == frame).Easing;
            return _definition.Set(item, current.SetKey(new Keyframe<double>(frame, number, easing)));
        });
    }

    partial void OnEasingChanged(EasingChoice? value)
    {
        if (value is null || !HasKey)
        {
            return;
        }

        var frame = _frame;
        Apply(item =>
        {
            var current = _definition.Get(item);
            var key = current.Keys.FirstOrDefault(candidate => candidate.Frame == frame);
            return current.IsAnimated && current.Keys.Any(candidate => candidate.Frame == frame)
                ? _definition.Set(item, current.SetKey(key with { Easing = value.Easing }))
                : item;
        });
    }
}

/// <summary>イージングの選択肢</summary>
public sealed record EasingChoice(Easing Easing, string Name)
{
    public static IReadOnlyList<EasingChoice> All { get; } =
    [
        new(Core.Animation.Easing.Linear, "直線"),
        new(Core.Animation.Easing.Hold, "保持（次で切り替え）"),
        new(new Easing(EasingCurve.Sine, EasingMode.InOut), "なめらか（加速・減速）"),
        new(new Easing(EasingCurve.Sine, EasingMode.In), "加速"),
        new(new Easing(EasingCurve.Sine, EasingMode.Out), "減速"),
        new(new Easing(EasingCurve.Cubic, EasingMode.InOut), "強めに加速・減速"),
        new(new Easing(EasingCurve.Back, EasingMode.Out), "行き過ぎて戻る"),
        new(new Easing(EasingCurve.Elastic, EasingMode.Out), "弾む（ばね）"),
        new(new Easing(EasingCurve.Bounce, EasingMode.Out), "跳ねる"),
    ];

    public override string ToString() => Name;
}

public sealed partial class NumberEditor(NumberProperty definition, ApplyChange apply) : PropertyEditor(definition.Label, apply)
{
    public decimal Minimum => NumberRange.ToDecimal(definition.Min);

    public decimal Maximum => NumberRange.ToDecimal(definition.Max);

    public decimal Increment => (decimal)definition.Increment;

    public string Format => definition.Format;

    [ObservableProperty]
    public partial decimal? Value { get; set; }

    protected override void Load(Item item, long localFrame) => Value = NumberRange.ToDecimal(definition.Get(item));

    partial void OnValueChanged(decimal? value)
    {
        if (value is { } number)
        {
            Apply(item => definition.Set(item, Math.Clamp((double)number, definition.Min, definition.Max)));
        }
    }
}

/// <summary>色。#RRGGBB か #RRGGBBAA で入れる</summary>
public sealed partial class ColorEditor(ColorProperty definition, ApplyChange apply) : PropertyEditor(definition.Label, apply)
{
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial Avalonia.Media.Color Swatch { get; set; }

    public static string Format(RgbaColor color) =>
        color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    public static bool TryParse(string text, out RgbaColor color)
    {
        color = default;
        text = text.Trim().TrimStart('#');
        if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (text.Length == 6)
        {
            value = (value << 8) | 0xFF;
        }

        color = new RgbaColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    protected override void Load(Item item, long localFrame)
    {
        var color = definition.Get(item);
        Text = Format(color);
        Swatch = Avalonia.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    partial void OnTextChanged(string value)
    {
        if (TryParse(value, out var color))
        {
            Swatch = Avalonia.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
            Apply(item => definition.Set(item, color));
        }
    }
}

public sealed partial class BoolEditor(BoolProperty definition, ApplyChange apply) : PropertyEditor(definition.Label, apply)
{
    [ObservableProperty]
    public partial bool Value { get; set; }

    protected override void Load(Item item, long localFrame) => Value = definition.Get(item);

    partial void OnValueChanged(bool value) => Apply(item => definition.Set(item, value));
}

/// <summary>選択肢の 1 つ</summary>
public sealed record Choice(object Value, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class ChoiceEditor : PropertyEditor
{
    private readonly ChoiceProperty _definition;

    public ChoiceEditor(ChoiceProperty definition, ApplyChange apply)
        : base(definition.Label, apply)
    {
        _definition = definition;
        Choices = [.. definition.Choices.Select(choice => new Choice(choice.Value, choice.Name))];
    }

    public IReadOnlyList<Choice> Choices { get; }

    [ObservableProperty]
    public partial Choice? Selected { get; set; }

    protected override void Load(Item item, long localFrame)
    {
        var value = _definition.Get(item);
        Selected = Choices.FirstOrDefault(choice => Equals(choice.Value, value));
    }

    partial void OnSelectedChanged(Choice? value)
    {
        if (value is not null)
        {
            Apply(item => _definition.Set(item, value.Value));
        }
    }
}

/// <summary>文字。入力し終えたとき（Enter・フォーカスが外れたとき）に変更を伝える</summary>
public sealed partial class TextEditor(TextProperty definition, ApplyChange apply) : PropertyEditor(definition.Label, apply)
{
    public bool Multiline => definition.Multiline;

    public string? Placeholder => definition.Placeholder;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    protected override void Load(Item item, long localFrame) => Text = definition.Get(item);

    /// <summary>入力し終えた</summary>
    public void Commit()
    {
        var text = Text;
        Apply(item => definition.Get(item) == text ? item : definition.Set(item, text));
    }
}

/// <summary>ファイル（LUT など）。「参照」で選ぶ</summary>
public sealed partial class FileEditor(FileProperty definition, ApplyChange apply, IDialogService dialogs) : PropertyEditor(definition.Label, apply)
{
    [ObservableProperty]
    public partial string Path { get; set; } = "";

    protected override void Load(Item item, long localFrame) => Path = definition.Get(item);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await dialogs.PickOpenFileAsync(Label, definition.Kinds) is { } path)
        {
            Path = path;
            Apply(item => definition.Set(item, path));
        }
    }
}

/// <summary>ファイル（LUT など）</summary>
public sealed record FileProperty(string Group, string Label, IReadOnlyList<FileKind> Kinds, Func<Item, string> Get, Func<Item, string, Item> Set)
    : PropertyDefinition(Group, Label);

/// <summary>数値の入力欄（decimal）との変換。範囲の端は ±1 兆に収める</summary>
public static class NumberRange
{
    private const double Limit = 1e12;

    public static decimal ToDecimal(double value) => double.IsNaN(value) ? 0 : (decimal)Math.Clamp(value, -Limit, Limit);
}
