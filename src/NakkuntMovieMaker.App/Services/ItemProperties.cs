using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.App.Services;

/// <summary>プロパティの定義。プロパティパネルは、この定義から入力欄を作る。Group は見出し、Label は項目の名前</summary>
public abstract record PropertyDefinition(string Group, string Label);

/// <summary>キーフレームでアニメーションできる数値</summary>
public sealed record AnimatedNumberProperty(
    string Group, string Label, Func<Item, Animatable<double>> Get, Func<Item, Animatable<double>, Item> Set,
    double Min = double.MinValue, double Max = double.MaxValue, double Increment = 1, string Format = "0.##")
    : PropertyDefinition(Group, Label);

/// <summary>数値</summary>
public sealed record NumberProperty(
    string Group, string Label, Func<Item, double> Get, Func<Item, double, Item> Set,
    double Min = double.MinValue, double Max = double.MaxValue, double Increment = 1, string Format = "0.##")
    : PropertyDefinition(Group, Label);

public sealed record ColorProperty(string Group, string Label, Func<Item, RgbaColor> Get, Func<Item, RgbaColor, Item> Set)
    : PropertyDefinition(Group, Label);

public sealed record BoolProperty(string Group, string Label, Func<Item, bool> Get, Func<Item, bool, Item> Set)
    : PropertyDefinition(Group, Label);

/// <summary>選択肢から 1 つ。Choices は（値, 表示名）</summary>
public sealed record ChoiceProperty(
    string Group, string Label, IReadOnlyList<(object Value, string Name)> Choices, Func<Item, object> Get, Func<Item, object, Item> Set)
    : PropertyDefinition(Group, Label);

public sealed record TextProperty(string Group, string Label, Func<Item, string> Get, Func<Item, string, Item> Set, bool Multiline = false, string? Placeholder = null)
    : PropertyDefinition(Group, Label);

/// <summary>
/// 組み込みのアイテムのプロパティの定義。種類ごとに、共通の項目（タイミング・変形）と、その種類の項目を並べる。
/// フォントとキャラクターの選択肢は、開いているプロジェクトと PC によって変わるので、呼ぶたびに作る
/// </summary>
public static class ItemProperties
{
    private const string Timing = "タイミング";
    private const string Transform = "位置・変形";
    private const string Crop = "切り抜き";
    private const string Text = "テキスト";
    private const string Outline = "縁取り・影・背景";
    private const string Shape = "図形";
    private const string Playback = "再生";
    private const string Sound = "音";
    private const string Voice = "セリフ";

    /// <summary>アイテムのプロパティの定義（上から表示する順）</summary>
    public static IReadOnlyList<PropertyDefinition> For(Item item, Project project, IReadOnlyList<string> fonts)
    {
        var list = new List<PropertyDefinition>
        {
            new NumberProperty(Timing, "開始（フレーム）", i => i.Start, (i, v) => i with { Start = Math.Max(0, (long)Math.Round(v)) }, 0, long.MaxValue, 1, "0"),
            new NumberProperty(Timing, "長さ（フレーム）", i => i.Length, (i, v) => i with { Length = Math.Max(1, (long)Math.Round(v)) }, 1, long.MaxValue, 1, "0"),
        };

        if (item is VisualItem)
        {
            list.AddRange(TransformProperties());
        }

        switch (item)
        {
            case TextItem:
                list.Add(new TextProperty(Text, "本文", i => ((TextItem)i).Text, (i, v) => ((TextItem)i) with { Text = v }, Multiline: true));
                list.AddRange(StyleProperties(fonts, i => ((TextItem)i).Style, (i, s) => ((TextItem)i) with { Style = s }));
                break;
            case ShapeItem:
                list.AddRange(ShapeProperties());
                break;
            case VideoItem:
                list.AddRange(PlaybackProperties(i => ((VideoItem)i).Playback, (i, p) => ((VideoItem)i) with { Playback = p }));
                list.AddRange(MixProperties(i => ((VideoItem)i).Audio, (i, m) => ((VideoItem)i) with { Audio = m }));
                break;
            case AudioItem:
                list.AddRange(PlaybackProperties(i => ((AudioItem)i).Playback, (i, p) => ((AudioItem)i) with { Playback = p }));
                list.AddRange(MixProperties(i => ((AudioItem)i).Audio, (i, m) => ((AudioItem)i) with { Audio = m }));
                break;
            case VoiceItem:
                list.Add(new ChoiceProperty(Voice, "キャラクター", Characters(project), i => ((VoiceItem)i).Character, (i, v) => ((VoiceItem)i) with { Character = (CharacterId)v }));
                list.Add(new TextProperty(Voice, "本文", i => ((VoiceItem)i).Text, (i, v) => ((VoiceItem)i) with { Text = v }, Multiline: true));
                list.Add(new TextProperty(Voice, "読み", i => ((VoiceItem)i).Reading ?? "", (i, v) => ((VoiceItem)i) with { Reading = v.Length == 0 ? null : v }, Placeholder: "空なら本文から自動で作る"));
                list.AddRange(MixProperties(i => ((VoiceItem)i).Audio, (i, m) => ((VoiceItem)i) with { Audio = m }));
                break;
            case TachieItem:
                list.Add(new ChoiceProperty(Voice, "キャラクター", Characters(project), i => ((TachieItem)i).Character, (i, v) => ((TachieItem)i) with { Character = (CharacterId)v }));
                break;
        }

        return list;
    }

    /// <summary>セリフの字幕のスタイル（キャラクターの設定でも使う）</summary>
    public static IEnumerable<PropertyDefinition> StyleProperties(IReadOnlyList<string> fonts, Func<Item, TextStyle> get, Func<Item, TextStyle, Item> set)
    {
        Func<Item, T> G<T>(Func<TextStyle, T> f) => i => f(get(i));
        Func<Item, T, Item> S<T>(Func<TextStyle, T, TextStyle> f) => (i, v) => set(i, f(get(i), v));

        IReadOnlyList<(object, string)> fontChoices = [("", "（既定のフォント）"), .. fonts.Select(font => ((object)font, font))];
        yield return new ChoiceProperty(Text, "フォント", fontChoices, G<object>(s => s.FontFamily), S<object>((s, v) => s with { FontFamily = (string)v }));
        yield return new NumberProperty(Text, "大きさ", G(s => s.FontSize), S<double>((s, v) => s with { FontSize = v }), 1, 1000);
        yield return new ColorProperty(Text, "色", G(s => s.Color), S<RgbaColor>((s, v) => s with { Color = v }));
        yield return new BoolProperty(Text, "太字", G(s => s.Bold), S<bool>((s, v) => s with { Bold = v }));
        yield return new BoolProperty(Text, "斜体", G(s => s.Italic), S<bool>((s, v) => s with { Italic = v }));
        yield return new ChoiceProperty(
            Text, "揃え", [(TextAlignment.Left, "左"), (TextAlignment.Center, "中央"), (TextAlignment.Right, "右")],
            G<object>(s => s.Alignment), S<object>((s, v) => s with { Alignment = (TextAlignment)v }));
        yield return new NumberProperty(Text, "行間（倍）", G(s => s.LineSpacing), S<double>((s, v) => s with { LineSpacing = v }), 0.1, 10, 0.1);
        yield return new NumberProperty(Text, "字間", G(s => s.LetterSpacing), S<double>((s, v) => s with { LetterSpacing = v }), -200, 1000);
        yield return new BoolProperty(Text, "縦書き", G(s => s.Vertical), S<bool>((s, v) => s with { Vertical = v }));
        yield return new NumberProperty(Text, "一文字ずつ表示（文字/秒）", G(s => s.RevealCharsPerSecond), S<double>((s, v) => s with { RevealCharsPerSecond = v }), 0, 1000);

        yield return new NumberProperty(Outline, "縁取りの太さ", G(s => s.Outline?.Width ?? 0), S<double>((s, v) => s with { Outline = v <= 0 ? null : new TextOutline(s.Outline?.Color ?? RgbaColor.Black, v) }), 0, 200);
        yield return new ColorProperty(Outline, "縁取りの色", G(s => s.Outline?.Color ?? RgbaColor.Black), S<RgbaColor>((s, v) => s with { Outline = new TextOutline(v, s.Outline?.Width ?? 4) }));
        yield return new BoolProperty(Outline, "影", G(s => s.Shadow is not null), S<bool>((s, v) => s with { Shadow = v ? s.Shadow ?? new TextShadow(new RgbaColor(0, 0, 0, 160), 4, 4, 4) : null }));
        yield return new ColorProperty(Outline, "影の色", G(s => s.Shadow?.Color ?? new RgbaColor(0, 0, 0, 160)), S<RgbaColor>((s, v) => s with { Shadow = (s.Shadow ?? new TextShadow(v, 4, 4, 4)) with { Color = v } }));
        yield return new NumberProperty(Outline, "影の位置 X", G(s => s.Shadow?.OffsetX ?? 4), S<double>((s, v) => s with { Shadow = (s.Shadow ?? new TextShadow(new RgbaColor(0, 0, 0, 160), 4, 4, 4)) with { OffsetX = v } }), -500, 500);
        yield return new NumberProperty(Outline, "影の位置 Y", G(s => s.Shadow?.OffsetY ?? 4), S<double>((s, v) => s with { Shadow = (s.Shadow ?? new TextShadow(new RgbaColor(0, 0, 0, 160), 4, 4, 4)) with { OffsetY = v } }), -500, 500);
        yield return new NumberProperty(Outline, "影のぼかし", G(s => s.Shadow?.Blur ?? 4), S<double>((s, v) => s with { Shadow = (s.Shadow ?? new TextShadow(new RgbaColor(0, 0, 0, 160), 4, 4, 4)) with { Blur = v } }), 0, 200);
        yield return new BoolProperty(Outline, "背景", G(s => s.Background is not null), S<bool>((s, v) => s with { Background = v ? s.Background ?? new TextBackground(new RgbaColor(0, 0, 0, 160)) : null }));
        yield return new ColorProperty(Outline, "背景の色", G(s => s.Background?.Color ?? new RgbaColor(0, 0, 0, 160)), S<RgbaColor>((s, v) => s with { Background = (s.Background ?? new TextBackground(v)) with { Color = v } }));
        yield return new NumberProperty(Outline, "背景の余白", G(s => s.Background?.Padding ?? 8), S<double>((s, v) => s with { Background = (s.Background ?? new TextBackground(new RgbaColor(0, 0, 0, 160))) with { Padding = v } }), 0, 500);
        yield return new NumberProperty(Outline, "背景の角丸", G(s => s.Background?.CornerRadius ?? 0), S<double>((s, v) => s with { Background = (s.Background ?? new TextBackground(new RgbaColor(0, 0, 0, 160))) with { CornerRadius = v } }), 0, 500);
    }

    /// <summary>キャラクターの選択肢</summary>
    public static IReadOnlyList<(object Value, string Name)> Characters(Project project) =>
        [.. project.Characters.Values.OrderBy(character => character.Name, StringComparer.CurrentCulture).Select(character => ((object)character.Id, character.Name))];

    private static IEnumerable<PropertyDefinition> TransformProperties()
    {
        static Func<Item, Animatable<double>> G(Func<Transform2D, Animatable<double>> f) => i => f(((VisualItem)i).Transform);
        static Func<Item, Animatable<double>, Item> S(Func<Transform2D, Animatable<double>, Transform2D> f) =>
            (i, v) => (VisualItem)i with { Transform = f(((VisualItem)i).Transform, v) };

        yield return new AnimatedNumberProperty(Transform, "X", G(t => t.X), S((t, v) => t with { X = v }));
        yield return new AnimatedNumberProperty(Transform, "Y", G(t => t.Y), S((t, v) => t with { Y = v }));
        yield return new AnimatedNumberProperty(Transform, "拡大率 X", G(t => t.ScaleX), S((t, v) => t with { ScaleX = v }), -100, 100, 0.01);
        yield return new AnimatedNumberProperty(Transform, "拡大率 Y", G(t => t.ScaleY), S((t, v) => t with { ScaleY = v }), -100, 100, 0.01);
        yield return new AnimatedNumberProperty(Transform, "回転（度）", G(t => t.Rotation), S((t, v) => t with { Rotation = v }));
        yield return new AnimatedNumberProperty(Transform, "中心点 X", G(t => t.AnchorX), S((t, v) => t with { AnchorX = v }));
        yield return new AnimatedNumberProperty(Transform, "中心点 Y", G(t => t.AnchorY), S((t, v) => t with { AnchorY = v }));
        yield return new AnimatedNumberProperty(Transform, "不透明度", i => ((VisualItem)i).Opacity, (i, v) => (VisualItem)i with { Opacity = v }, 0, 1, 0.01);
        yield return new BoolProperty(Transform, "左右反転", i => ((VisualItem)i).Transform.FlipHorizontal, (i, v) => (VisualItem)i with { Transform = ((VisualItem)i).Transform with { FlipHorizontal = v } });
        yield return new BoolProperty(Transform, "上下反転", i => ((VisualItem)i).Transform.FlipVertical, (i, v) => (VisualItem)i with { Transform = ((VisualItem)i).Transform with { FlipVertical = v } });
        yield return new ChoiceProperty(
            Transform, "合成モード",
            [
                (BlendMode.Normal, "通常"), (BlendMode.Add, "加算"), (BlendMode.Subtract, "減算"), (BlendMode.Multiply, "乗算"),
                (BlendMode.Screen, "スクリーン"), (BlendMode.Overlay, "オーバーレイ"), (BlendMode.Lighten, "比較（明）"),
                (BlendMode.Darken, "比較（暗）"), (BlendMode.Difference, "差分"),
            ],
            i => ((VisualItem)i).Blend, (i, v) => (VisualItem)i with { Blend = (BlendMode)v });

        yield return new NumberProperty(Crop, "左", i => ((VisualItem)i).Crop.Left, (i, v) => (VisualItem)i with { Crop = ((VisualItem)i).Crop with { Left = v } }, 0);
        yield return new NumberProperty(Crop, "上", i => ((VisualItem)i).Crop.Top, (i, v) => (VisualItem)i with { Crop = ((VisualItem)i).Crop with { Top = v } }, 0);
        yield return new NumberProperty(Crop, "右", i => ((VisualItem)i).Crop.Right, (i, v) => (VisualItem)i with { Crop = ((VisualItem)i).Crop with { Right = v } }, 0);
        yield return new NumberProperty(Crop, "下", i => ((VisualItem)i).Crop.Bottom, (i, v) => (VisualItem)i with { Crop = ((VisualItem)i).Crop with { Bottom = v } }, 0);
    }

    private static IEnumerable<PropertyDefinition> ShapeProperties()
    {
        static Func<Item, T> G<T>(Func<ShapeStyle, T> f) => i => f(((ShapeItem)i).Shape);
        static Func<Item, T, Item> S<T>(Func<ShapeStyle, T, ShapeStyle> f) => (i, v) => ((ShapeItem)i) with { Shape = f(((ShapeItem)i).Shape, v) };

        yield return new ChoiceProperty(
            Shape, "種類",
            [.. Enum.GetValues<ShapeKind>().Select(kind => ((object)kind, ItemLabels.ShapeName(kind)))],
            G<object>(s => s.Kind), S<object>((s, v) => s with { Kind = (ShapeKind)v }));
        yield return new NumberProperty(Shape, "幅", G(s => s.Width), S<double>((s, v) => s with { Width = v }), 0, 100000);
        yield return new NumberProperty(Shape, "高さ", G(s => s.Height), S<double>((s, v) => s with { Height = v }), 0, 100000);
        yield return new BoolProperty(Shape, "グラデーション", G(s => s.Fill is LinearGradientPaint), S<bool>((s, v) => s with
        {
            Fill = v
                ? s.Fill as LinearGradientPaint ?? new LinearGradientPaint(FillColor(s.Fill), RgbaColor.Black)
                : new SolidPaint(FillColor(s.Fill)),
        }));
        yield return new ColorProperty(Shape, "塗りの色", G(s => FillColor(s.Fill)), S<RgbaColor>((s, v) => s with
        {
            Fill = s.Fill is LinearGradientPaint gradient ? gradient with { Start = v } : new SolidPaint(v),
        }));
        yield return new ColorProperty(Shape, "グラデーションの終わりの色", G(s => (s.Fill as LinearGradientPaint)?.End ?? RgbaColor.Black), S<RgbaColor>((s, v) => s with
        {
            Fill = s.Fill is LinearGradientPaint gradient ? gradient with { End = v } : new LinearGradientPaint(FillColor(s.Fill), v),
        }));
        yield return new NumberProperty(Shape, "グラデーションの角度", G(s => (s.Fill as LinearGradientPaint)?.Angle ?? 0), S<double>((s, v) => s with
        {
            Fill = s.Fill is LinearGradientPaint gradient ? gradient with { Angle = v } : s.Fill,
        }), -360, 360);
        yield return new NumberProperty(Shape, "線の太さ", G(s => s.StrokeWidth), S<double>((s, v) => s with { StrokeWidth = v, StrokeColor = s.StrokeColor ?? RgbaColor.Black }), 0, 1000);
        yield return new ColorProperty(Shape, "線の色", G(s => s.StrokeColor ?? RgbaColor.Black), S<RgbaColor>((s, v) => s with { StrokeColor = v }));
        yield return new NumberProperty(Shape, "角丸", G(s => s.CornerRadius), S<double>((s, v) => s with { CornerRadius = v }), 0, 10000);
        yield return new NumberProperty(Shape, "角の数", G(s => (double)s.Sides), S<double>((s, v) => s with { Sides = (int)Math.Round(v) }), 3, 64, 1, "0");
        yield return new NumberProperty(Shape, "星のへこみ", G(s => s.InnerRadiusRatio), S<double>((s, v) => s with { InnerRadiusRatio = v }), 0.01, 1, 0.05);
    }

    private static RgbaColor FillColor(Paint paint) => paint switch
    {
        SolidPaint solid => solid.Color,
        LinearGradientPaint gradient => gradient.Start,
        _ => RgbaColor.White,
    };

    private static IEnumerable<PropertyDefinition> PlaybackProperties(Func<Item, MediaPlayback> get, Func<Item, MediaPlayback, Item> set)
    {
        yield return new NumberProperty(
            Playback, "速度（倍）", i => get(i).Speed.ToDouble(),
            (i, v) => set(i, get(i) with { Speed = new Rational((long)Math.Round(Math.Clamp(v, 0.01, 100) * 1000), 1000) }), 0.01, 100, 0.1);
        yield return new NumberProperty(
            Playback, "素材の開始位置（秒）", i => get(i).SourceOffset.ToDouble(),
            (i, v) => set(i, get(i) with { SourceOffset = new Rational((long)Math.Round(Math.Max(0, v) * 1000), 1000) }), 0, double.MaxValue, 0.1, "0.###");
        yield return new BoolProperty(Playback, "逆再生", i => get(i).Reverse, (i, v) => set(i, get(i) with { Reverse = v }));
    }

    private static IEnumerable<PropertyDefinition> MixProperties(Func<Item, AudioMix> get, Func<Item, AudioMix, Item> set)
    {
        yield return new AnimatedNumberProperty(Sound, "音量（倍）", i => get(i).Volume, (i, v) => set(i, get(i) with { Volume = v }), 0, 10, 0.05);
        yield return new AnimatedNumberProperty(Sound, "パン（左 -1〜右 1）", i => get(i).Pan, (i, v) => set(i, get(i) with { Pan = v }), -1, 1, 0.05);
        yield return new NumberProperty(Sound, "フェードイン（フレーム）", i => get(i).FadeInFrames, (i, v) => set(i, get(i) with { FadeInFrames = (long)Math.Round(v) }), 0, long.MaxValue, 1, "0");
        yield return new NumberProperty(Sound, "フェードアウト（フレーム）", i => get(i).FadeOutFrames, (i, v) => set(i, get(i) with { FadeOutFrames = (long)Math.Round(v) }), 0, long.MaxValue, 1, "0");
        yield return new BoolProperty(Sound, "速度を変えても音の高さを保つ", i => get(i).PreservePitch, (i, v) => set(i, get(i) with { PreservePitch = v }));
        yield return new NumberProperty(Sound, "セリフの間は下げる（dB）", i => get(i).DuckingDb, (i, v) => set(i, get(i) with { DuckingDb = v }), 0, 60, 1);
    }
}
