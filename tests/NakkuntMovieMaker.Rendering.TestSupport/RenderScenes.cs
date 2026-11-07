using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Transitions;

namespace NakkuntMovieMaker.Rendering.TestSupport;

/// <summary>
/// 描く場面と、その素材。Name はゴールデン画像の名前と同じ。RequiresFonts は同梱のフォントが要るか。
/// 破棄すると、場面のために作った一時ファイル（LUT など）を消す
/// </summary>
public sealed class RenderScene(string name, FramePlan plan, TestMedia media, double scale, bool requiresFonts, IDisposable? resources) : IDisposable
{
    public string Name => name;

    public FramePlan Plan => plan;

    public TestMedia Media => media;

    public double Scale => scale;

    public bool RequiresFonts => requiresFonts;

    public void Dispose() => resources?.Dispose();

    public override string ToString() => name;
}

/// <summary>
/// ゴールデン画像の場面（M1 の 29 枚）。CPU 版はゴールデン画像と比べ、GPU 版は同じ場面を CPU 版で描いた結果と比べる
/// </summary>
public static class RenderScenes
{
    private static readonly RgbaColor Backdrop = new(40, 40, 48);

    public static IReadOnlyList<string> Names { get; } =
    [
        "composition",
        "composition-half",
        "shapes",
        "text-horizontal",
        "text-styles",
        "text-vertical",
        .. BuiltInEffects.All.Select(effect => "effect-" + effect.Type["nmm.".Length..]),
        .. BuiltInTransitions.All.Select(transition => "transition-" + transition.Type["nmm.".Length..]),
    ];

    /// <summary>リニアのモード（合成をリニアで計算する）の場面。ゴールデン画像は Golden/linear にある</summary>
    public static IReadOnlyList<string> LinearNames { get; } =
    [
        "linear/composition",
        "linear/shapes",
        "linear/effect-blur",
        "linear/effect-glow",
        "linear/transition-crossfade",
        "linear/transition-wipe",
    ];

    /// <summary>出力の変換（プレビューの表示・書き出しの変換）をかける場面。ゴールデン画像は Golden/output にある</summary>
    public static IReadOnlyList<string> OutputNames { get; } =
    [
        "output/composition",
        "output/transition-crossfade",
    ];

    public static RenderScene Create(string name) => name switch
    {
        _ when name.StartsWith("linear/", StringComparison.Ordinal) => Linear(name),
        _ when name.StartsWith("output/", StringComparison.Ordinal) => Output(name),
        "composition" => Composition(name, 1),
        "composition-half" => Composition(name, 0.5),
        "shapes" => Shapes(name),
        "text-horizontal" or "text-styles" or "text-vertical" => Texts(name),
        _ when name.StartsWith("effect-", StringComparison.Ordinal) => Effect(name),
        _ when name.StartsWith("transition-", StringComparison.Ordinal) => Transition(name),
        _ => throw new ArgumentException($"場面がありません: {name}", nameof(name)),
    };

    private static RenderScene Make(string name, Scene scene, TestMedia media, long frame = 0, RgbaColor? background = null, double scale = 1, bool fonts = false, IDisposable? resources = null) =>
        new(name, TimelineEvaluator.Evaluate(scene, frame, background), media, scale, fonts, resources);

    // 同じ場面を描いてから、テスト用の出力の変換をかける
    private static RenderScene Output(string name)
    {
        var scene = Create(name["output/".Length..]);
        return new RenderScene(name, scene.Plan with { Color = scene.Plan.Color.WithOutput(TestLuts.Grade) }, scene.Media, scene.Scale, scene.RequiresFonts, scene);
    }

    // 同じ場面を、リニアのモードで描く
    private static RenderScene Linear(string name)
    {
        var scene = Create(name["linear/".Length..]);
        return new RenderScene(name, scene.Plan with { Color = FrameColor.Linear }, scene.Media, scene.Scale, scene.RequiresFonts, scene);
    }

    private static Scene With(Scene scene, params Item[] items) =>
        scene with { Items = scene.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };

    // 3 つの画像を、変形・不透明度・切り抜き・反転・合成モードを付けて重ねる（320×180）
    private static RenderScene Composition(string name, double scale)
    {
        var empty = Scene.CreateNew("テスト", new SceneFormat(320, 180, 30, 48000), 3);
        var media = new TestMedia();
        ImageItem Image(BgraImage image, int layer) => new(ItemId.New(), empty.Layers[layer].Id, 0, 30, media.AddImage(new SourceImage(image)));
        var scene = With(
            empty,
            Image(TestImages.Gradient(160, 90), 0) with
            {
                Transform = Transform2D.Identity with { ScaleX = 1.5, ScaleY = 1.5, Rotation = 15.0 },
            },
            Image(TestImages.Quadrants(64, 64), 1) with
            {
                Transform = Transform2D.Identity with { X = -80.0, Y = 20.0, FlipHorizontal = true, Rotation = -20.0 },
                Opacity = 0.8,
            },
            Image(TestImages.Halves(80, 60, (255, 0, 0, 255), (0, 0, 255, 255)), 2) with
            {
                Transform = Transform2D.Identity with { X = 90.0, Y = 40.0, Rotation = -30.0, AnchorX = 20.0, ScaleX = 0.75 },
                Crop = new Crop(10, 5, 0, 15),
                Blend = BlendMode.Screen,
            });
        return Make(name, scene, media, background: Backdrop, scale: scale);
    }

    // 図形の種類と、塗り・線・グラデーション（320×180）
    private static RenderScene Shapes(string name)
    {
        var empty = Scene.CreateNew("テスト", new SceneFormat(320, 180, 30, 48000), 10);
        ShapeItem Shape(ShapeStyle shape, int layer, double x, double y) =>
            new(ItemId.New(), empty.Layers[layer].Id, 0, 30, shape) { Transform = Transform2D.Identity with { X = x, Y = y } };
        var white = new SolidPaint(RgbaColor.White);
        var orange = new RgbaColor(255, 140, 0);
        var scene = With(
            empty,
            Shape(new ShapeStyle(ShapeKind.Rectangle, 70, 50, new SolidPaint(new RgbaColor(80, 160, 255)), RgbaColor.White, 3, CornerRadius: 12), 0, -110, -45),
            Shape(new ShapeStyle(ShapeKind.Ellipse, 70, 50, new LinearGradientPaint(orange, new RgbaColor(200, 0, 120), 45)), 1, -30, -45),
            Shape(new ShapeStyle(ShapeKind.Triangle, 60, 55, white, orange, 4), 2, 45, -45),
            Shape(new ShapeStyle(ShapeKind.Polygon, 60, 60, new SolidPaint(new RgbaColor(120, 220, 120)), Sides: 6), 3, 115, -45),
            Shape(new ShapeStyle(ShapeKind.Star, 64, 64, new SolidPaint(new RgbaColor(255, 220, 0)), new RgbaColor(160, 80, 0), 2, Sides: 5, InnerRadiusRatio: 0.45), 4, -110, 40),
            Shape(new ShapeStyle(ShapeKind.Line, 80, 6, white), 5, -30, 40),
            Shape(new ShapeStyle(ShapeKind.Arrow, 80, 40, new SolidPaint(new RgbaColor(255, 80, 80))), 6, 55, 40),
            Shape(new ShapeStyle(ShapeKind.Star, 50, 50, new SolidPaint(new RgbaColor(200, 120, 255)), Sides: 8, InnerRadiusRatio: 0.7), 7, 125, 40));
        return Make(name, scene, new TestMedia(), background: new RgbaColor(32, 36, 48));
    }

    // 文字（縁取り・影・背景、揃えと太字・斜体・字間・行間、縦書き）（320×180）
    private static RenderScene Texts(string name)
    {
        var empty = Scene.CreateNew("テスト", new SceneFormat(320, 180, 30, 48000), 3);
        static TextStyle Style(double size) => new("", size, RgbaColor.White);
        TextItem Text(string text, TextStyle style, int layer = 0, Transform2D? transform = null) =>
            new(ItemId.New(), empty.Layers[layer].Id, 0, 90, text) { Style = style, Transform = transform ?? Transform2D.Identity };

        Item[] items = name switch
        {
            "text-horizontal" =>
            [
                Text("ゆっくり解説の字幕\nNakkunt Movie Maker 123", Style(20) with
                {
                    Outline = new TextOutline(new RgbaColor(200, 40, 80), 3),
                    Shadow = new TextShadow(new RgbaColor(0, 0, 0, 160), 4, 4, Blur: 2),
                    Background = new TextBackground(new RgbaColor(255, 255, 255, 60), Padding: 8, CornerRadius: 10),
                }),
            ],
            "text-styles" =>
            [
                Text("左揃え・太字\n2 行目", Style(22) with { Bold = true, Alignment = TextAlignment.Left }, 0, Transform2D.Identity with { X = -80.0, Y = -50.0 }),
                Text("右揃え・斜体\nItalic", Style(22) with { Italic = true, Alignment = TextAlignment.Right, Color = new RgbaColor(255, 220, 120) }, 1, Transform2D.Identity with { X = 80.0, Y = -50.0 }),
                Text("字 間 と 行 間", Style(24) with { LetterSpacing = 6, LineSpacing = 1.6, Outline = new TextOutline(RgbaColor.Black, 2) }, 2, Transform2D.Identity with { Y = 50.0, Rotation = -8.0 }),
            ],
            _ => [Text("「縦書き」。\nラーメン、", Style(26) with { Vertical = true, Outline = new TextOutline(new RgbaColor(40, 40, 160), 2) })],
        };
        return Make(name, With(empty, items), new TestMedia(), background: new RgbaColor(30, 40, 60), fonts: true);
    }

    // エフェクトを 1 つかけた画像か星形（160×90）
    private static RenderScene Effect(string name)
    {
        var empty = Scene.CreateNew("テスト", new SceneFormat(160, 90, 30, 48000), 1);
        var media = new TestMedia();
        var files = new TempFiles();
        var subject = TestImages.Subject(96, 60);

        ImageItem Image(params EffectInstance[] effects) =>
            new(ItemId.New(), empty.Layers[0].Id, 0, 90, media.AddImage(subject)) { Effects = [.. effects] };
        ShapeItem Star(params EffectInstance[] effects) =>
            new(ItemId.New(), empty.Layers[0].Id, 0, 90, new ShapeStyle(ShapeKind.Star, 50, 50, new SolidPaint(new RgbaColor(255, 210, 40)), InnerRadiusRatio: 0.45))
            {
                Effects = [.. effects],
            };

        var type = "nmm." + name["effect-".Length..];
        var (item, frame) = type switch
        {
            "nmm.colorAdjust" => ((Item)Image(EffectOf(BuiltInEffects.ColorAdjust,
                ("brightness", 10), ("contrast", 30), ("saturation", 40), ("hue", 30), ("gamma", 1.2), ("temperature", 30))), 0L),
            "nmm.lut" => (Image(EffectOf(BuiltInEffects.Lut,
                ("file", files.Write("warm.cube", TestLuts.Cube(8, rgb => (Math.Pow(rgb.R, 0.8), rgb.G, rgb.B * 0.8)))), ("intensity", 80))), 0),
            "nmm.blur" => (Image(EffectOf(BuiltInEffects.Blur, ("radius", 8))), 0),
            "nmm.mosaic" => (Image(EffectOf(BuiltInEffects.Mosaic, ("size", 8))), 0),
            "nmm.sharpen" => (Image(EffectOf(BuiltInEffects.Sharpen, ("amount", 200), ("radius", 3))), 0),
            "nmm.chromaKey" => (Image(EffectOf(BuiltInEffects.ChromaKey)), 0),
            "nmm.colorKey" => (Image(EffectOf(BuiltInEffects.ColorKey, ("color", new RgbaColor(128, 128, 128)), ("tolerance", 15))), 0),
            "nmm.dropShadow" => (Star(EffectOf(BuiltInEffects.DropShadow)), 0),
            "nmm.border" => (Star(EffectOf(BuiltInEffects.Border, ("color", new RgbaColor(30, 120, 255)), ("width", 5), ("blur", 2))), 0),
            "nmm.glow" => (Star(EffectOf(BuiltInEffects.Glow, ("radius", 16), ("strength", 150), ("tint", true), ("color", new RgbaColor(255, 120, 0)))), 0),
            "nmm.mask" => (Image(EffectOf(BuiltInEffects.Mask, ("shape", "ellipse"), ("width", 80), ("height", 50), ("x", 5), ("feather", 10))), 0),
            "nmm.fade" => (Image(EffectOf(BuiltInEffects.Fade, ("fadeIn", 1.0))), 12),
            "nmm.noise" => (Image(EffectOf(BuiltInEffects.Noise, ("amount", 30), ("monochrome", false))), 3),
            "nmm.shake" => (Star(EffectOf(BuiltInEffects.Shake, ("amplitudeX", 15), ("amplitudeY", 10), ("rotation", 20), ("seed", 3))), 4),
            "nmm.monochrome" => (Image(EffectOf(BuiltInEffects.Monochrome, ("color", new RgbaColor(255, 230, 190)))), 0),
            _ => throw new ArgumentException($"場面がありません: {name}", nameof(name)),
        };

        return Make(name, With(empty, item), media, frame, Backdrop, resources: files);
    }

    // 前の画像から後の画像へのトランジション（160×90。半分の時点、回転と色を挟んだフェードは 1/4 の時点）
    private static RenderScene Transition(string name)
    {
        var empty = Scene.CreateNew("テスト", new SceneFormat(160, 90, 30, 48000), 1);
        var media = new TestMedia();
        var type = "nmm." + name["transition-".Length..];
        var definition = BuiltInTransitions.All.SingleOrDefault(transition => transition.Type == type)
            ?? throw new ArgumentException($"場面がありません: {name}", nameof(name));
        var from = new ImageItem(ItemId.New(), empty.Layers[0].Id, 0, 30, media.AddImage(TestImages.Subject(120, 72)));
        var to = new ImageItem(ItemId.New(), empty.Layers[0].Id, 30, 30, media.AddImage(TestImages.Quadrants(72, 72)))
        {
            Transition = new TransitionInstance(definition.Type, 20, ImmutableDictionary<string, ParameterValue>.Empty),
        };
        var frame = type == BuiltInTransitions.Spin.Type || type == BuiltInTransitions.Dip.Type ? 25 : 30;
        return Make(name, With(empty, from, to), media, frame);
    }

    /// <summary>エフェクトの値を名前で指定して作る（double・int・RgbaColor・bool・string）</summary>
    public static EffectInstance EffectOf(EffectDefinition definition, params (string Name, object Value)[] values) =>
        new(EffectId.New(), definition.Type, values.ToImmutableDictionary(
            value => value.Name,
            value => value.Value switch
            {
                double number => (ParameterValue)new NumberParameter(number),
                int number => new NumberParameter((double)number),
                RgbaColor color => new ColorParameter(color),
                bool flag => new BoolParameter(flag),
                string text => new TextParameter(text),
                _ => throw new ArgumentException("扱えない値です", nameof(values)),
            }));
}
