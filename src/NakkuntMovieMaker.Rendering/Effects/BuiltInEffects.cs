using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Rendering.Effects;

/// <summary>
/// 組み込みのエフェクトの定義。長さ・位置の単位は、アイテムのローカル座標のピクセル（素材のピクセル）
/// </summary>
public static class BuiltInEffects
{
    public static readonly EffectDefinition ColorAdjust = new Simple(
        "nmm.colorAdjust",
        "色調補正",
        ParameterDefinition.Number("brightness", "明るさ", 0, -100, 100),
        ParameterDefinition.Number("contrast", "コントラスト", 0, -100, 100),
        ParameterDefinition.Number("saturation", "彩度", 0, -100, 100),
        ParameterDefinition.Number("hue", "色相（度）", 0, -180, 180),
        ParameterDefinition.Number("gamma", "ガンマ", 1, 0.1, 10),
        ParameterDefinition.Number("temperature", "色温度", 0, -100, 100));

    public static readonly EffectDefinition Lut = new Simple(
        "nmm.lut",
        "LUT",
        ParameterDefinition.File("file", "LUT ファイル（.cube）"),
        ParameterDefinition.Number("intensity", "適用度（%）", 100, 0, 100));

    public static readonly EffectDefinition Blur = new Simple(
        "nmm.blur",
        "ぼかし",
        ParameterDefinition.Number("radius", "半径", 10, 0, 500));

    public static readonly EffectDefinition Mosaic = new Simple(
        "nmm.mosaic",
        "モザイク",
        ParameterDefinition.Number("size", "ブロックの大きさ", 16, 1, 1000));

    public static readonly EffectDefinition Sharpen = new Simple(
        "nmm.sharpen",
        "シャープ",
        ParameterDefinition.Number("amount", "強さ（%）", 100, 0, 500),
        ParameterDefinition.Number("radius", "半径", 2, 0.5, 50));

    public static readonly EffectDefinition ChromaKey = new Simple(
        "nmm.chromaKey",
        "クロマキー",
        ParameterDefinition.Color("color", "抜く色", new RgbaColor(0, 255, 0)),
        ParameterDefinition.Number("tolerance", "許容範囲", 30, 0, 100),
        ParameterDefinition.Number("softness", "境界のぼかし", 10, 0, 100),
        ParameterDefinition.Number("spill", "色かぶりの除去", 50, 0, 100));

    public static readonly EffectDefinition ColorKey = new Simple(
        "nmm.colorKey",
        "カラーキー",
        ParameterDefinition.Color("color", "抜く色", RgbaColor.White),
        ParameterDefinition.Number("tolerance", "許容範囲", 10, 0, 100),
        ParameterDefinition.Number("softness", "境界のぼかし", 5, 0, 100));

    public static readonly EffectDefinition DropShadow = new Simple(
        "nmm.dropShadow",
        "ドロップシャドウ",
        ParameterDefinition.Color("color", "色", RgbaColor.Black),
        ParameterDefinition.Number("offsetX", "ずらす量（X）", 6),
        ParameterDefinition.Number("offsetY", "ずらす量（Y）", 6),
        ParameterDefinition.Number("blur", "ぼかしの半径", 6, 0, 500),
        ParameterDefinition.Number("opacity", "濃さ（%）", 60, 0, 100));

    public static readonly EffectDefinition Border = new Simple(
        "nmm.border",
        "縁取り",
        ParameterDefinition.Color("color", "色", RgbaColor.White),
        ParameterDefinition.Number("width", "太さ", 4, 0, 500),
        ParameterDefinition.Number("blur", "ぼかしの半径", 0, 0, 500));

    public static readonly EffectDefinition Glow = new Simple(
        "nmm.glow",
        "発光",
        ParameterDefinition.Number("strength", "強さ（%）", 100, 0, 500),
        ParameterDefinition.Number("radius", "広がり", 12, 0, 500),
        ParameterDefinition.Bool("tint", "色を指定する", false),
        ParameterDefinition.Color("color", "光の色", RgbaColor.White));

    public static readonly EffectDefinition Mask = new Simple(
        "nmm.mask",
        "マスク",
        ParameterDefinition.Choice("shape", "形", "rectangle", "rectangle", "ellipse"),
        ParameterDefinition.Number("width", "幅", 200, 0),
        ParameterDefinition.Number("height", "高さ", 200, 0),
        ParameterDefinition.Number("x", "位置（X）", 0),
        ParameterDefinition.Number("y", "位置（Y）", 0),
        ParameterDefinition.Number("rotation", "回転（度）", 0),
        ParameterDefinition.Number("cornerRadius", "角の丸み", 0, 0),
        ParameterDefinition.Number("feather", "境界のぼかし", 0, 0, 500),
        ParameterDefinition.Bool("invert", "反転", false));

    public static readonly EffectDefinition Fade = new FadeDefinition();

    public static readonly EffectDefinition Noise = new NoiseDefinition();

    public static readonly EffectDefinition Shake = new ShakeDefinition();

    public static readonly EffectDefinition Monochrome = new Simple(
        "nmm.monochrome",
        "モノクロ",
        ParameterDefinition.Number("strength", "強さ（%）", 100, 0, 100),
        ParameterDefinition.Color("color", "色", RgbaColor.White));

    public static ImmutableArray<EffectDefinition> All { get; } =
    [
        ColorAdjust, Lut, Blur, Mosaic, Sharpen, ChromaKey, ColorKey, DropShadow, Border, Glow, Mask, Fade, Noise, Shake, Monochrome,
    ];

    // 0 以上 1 未満の、seed と番号から決まる乱数（どの環境でも同じ値になる）
    internal static double Random(long seed, long index, int channel)
    {
        var x = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)index * 0xC2B2AE3D27D4EB4FUL ^ (ulong)channel * 0x165667B19E3779F9UL);
        x ^= x >> 30;
        x = unchecked(x * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 27;
        x = unchecked(x * 0x94D049BB133111EBUL);
        x ^= x >> 31;
        return (x >> 11) * (1.0 / (1UL << 53));
    }

    private sealed class Simple(string type, string name, params ParameterDefinition[] parameters) : EffectDefinition
    {
        public override string Type => type;

        public override string Name => name;

        public override ImmutableArray<ParameterDefinition> Parameters { get; } = [.. parameters];
    }

    // フェードイン・フェードアウト。最初のフレームと最後のフレームが透明になり、"alpha" に不透明度の係数を入れる
    private sealed class FadeDefinition : EffectDefinition
    {
        public override string Type => "nmm.fade";

        public override string Name => "フェードイン・フェードアウト";

        public override ImmutableArray<ParameterDefinition> Parameters { get; } =
        [
            ParameterDefinition.Number("fadeIn", "フェードイン（秒）", 0.5, 0),
            ParameterDefinition.Number("fadeOut", "フェードアウト（秒）", 0.5, 0),
        ];

        public override EffectParameters Resolve(EffectParameters parameters, EffectTime time)
        {
            var fps = time.Fps.ToDouble();
            var fadeIn = parameters.GetNumber("fadeIn") * fps;
            var fadeOut = parameters.GetNumber("fadeOut") * fps;
            var alpha = 1.0;
            if (fadeIn > 0)
            {
                alpha = Math.Min(alpha, time.LocalFrame / fadeIn);
            }

            if (fadeOut > 0)
            {
                alpha = Math.Min(alpha, (time.Length - 1 - time.LocalFrame) / fadeOut);
            }

            return parameters.With("alpha", Math.Clamp(alpha, 0, 1));
        }
    }

    // ノイズ。"frame" に乱数を変えるための番号を入れる（動かさないときは 0）
    private sealed class NoiseDefinition : EffectDefinition
    {
        public override string Type => "nmm.noise";

        public override string Name => "ノイズ";

        public override ImmutableArray<ParameterDefinition> Parameters { get; } =
        [
            ParameterDefinition.Number("amount", "強さ（%）", 20, 0, 100),
            ParameterDefinition.Bool("monochrome", "白黒のノイズ", true),
            ParameterDefinition.Bool("animated", "フレームごとに変える", true),
            ParameterDefinition.Number("seed", "乱数の種", 0),
        ];

        public override EffectParameters Resolve(EffectParameters parameters, EffectTime time) =>
            parameters.With("frame", parameters.GetBool("animated") ? (double)time.LocalFrame : 0.0);
    }

    // 振動。1 秒に frequency 回、ずれと回転を乱数で決め直し、"offsetX"・"offsetY"・"angle" に入れる
    private sealed class ShakeDefinition : EffectDefinition
    {
        public override string Type => "nmm.shake";

        public override string Name => "振動";

        public override ImmutableArray<ParameterDefinition> Parameters { get; } =
        [
            ParameterDefinition.Number("amplitudeX", "揺れ幅（X）", 10, 0),
            ParameterDefinition.Number("amplitudeY", "揺れ幅（Y）", 10, 0),
            ParameterDefinition.Number("rotation", "回転の揺れ幅（度）", 0, 0, 180),
            ParameterDefinition.Number("frequency", "1 秒に揺れる回数", 15, 0.1, 120),
            ParameterDefinition.Number("seed", "乱数の種", 0),
        ];

        public override EffectParameters Resolve(EffectParameters parameters, EffectTime time)
        {
            var step = (long)Math.Floor(time.Seconds * parameters.GetNumber("frequency") + 1e-9);
            var seed = (long)parameters.GetNumber("seed");
            double Swing(int channel) => Random(seed, step, channel) * 2 - 1;
            return parameters
                .With("offsetX", Swing(0) * parameters.GetNumber("amplitudeX"))
                .With("offsetY", Swing(1) * parameters.GetNumber("amplitudeY"))
                .With("angle", Swing(2) * parameters.GetNumber("rotation"));
        }
    }
}
