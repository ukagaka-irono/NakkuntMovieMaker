using System.Numerics;
using NakkuntMovieMaker.Rendering.Color;
using NakkuntMovieMaker.Rendering.Effects;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

// 色だけを変えるエフェクト（画像の大きさは変わらない）

/// <summary>
/// 色調補正。ガンマ → 明るさ → コントラスト → 色温度 → 彩度 → 色相 の順にかける
/// </summary>
internal sealed class ColorAdjustEffect : ICpuEffect
{
    public string Type => BuiltInEffects.ColorAdjust.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var contrast = p.GetFloat("contrast") / 100f;
        var hue = p.GetFloat("hue") * MathF.PI / 180f;
        var shader = new Shader(
            InverseGamma: 1f / Math.Max(p.GetFloat("gamma"), 0.01f),
            Brightness: p.GetFloat("brightness") / 100f,
            Contrast: contrast >= 0 ? 1f / (1f - contrast * 0.99f) : 1f + contrast,
            Temperature: p.GetFloat("temperature") / 100f,
            Saturation: 1f + p.GetFloat("saturation") / 100f,
            HueCos: MathF.Cos(hue),
            HueSin: MathF.Sin(hue));
        if (shader.IsIdentity)
        {
            return input;
        }

        PixelOps.Map(input, shader);
        return input;
    }

    private readonly record struct Shader(
        float InverseGamma, float Brightness, float Contrast, float Temperature, float Saturation, float HueCos, float HueSin) : IPixelShader
    {
        public bool IsIdentity =>
            InverseGamma == 1 && Brightness == 0 && Contrast == 1 && Temperature == 0 && Saturation == 1 && HueSin == 0 && HueCos == 1;

        public void Apply(ref Vector4 color, int x, int y)
        {
            var rgb = new Vector3(color.X, color.Y, color.Z);
            if (InverseGamma != 1)
            {
                rgb = new Vector3(MathF.Pow(rgb.X, InverseGamma), MathF.Pow(rgb.Y, InverseGamma), MathF.Pow(rgb.Z, InverseGamma));
            }

            rgb += new Vector3(Brightness);
            rgb = (rgb - new Vector3(0.5f)) * Contrast + new Vector3(0.5f);
            rgb *= new Vector3(1 + Temperature * 0.2f, 1, 1 - Temperature * 0.2f);

            // 彩度と色相は YIQ で扱う（Y は明るさ、I・Q は色み）
            var luma = rgb.X * 0.299f + rgb.Y * 0.587f + rgb.Z * 0.114f;
            var i = (rgb.X * 0.596f - rgb.Y * 0.274f - rgb.Z * 0.322f) * Saturation;
            var q = (rgb.X * 0.211f - rgb.Y * 0.523f + rgb.Z * 0.312f) * Saturation;
            var rotatedI = i * HueCos - q * HueSin;
            var rotatedQ = i * HueSin + q * HueCos;
            color = new Vector4(
                luma + 0.956f * rotatedI + 0.621f * rotatedQ,
                luma - 0.272f * rotatedI - 0.647f * rotatedQ,
                luma - 1.106f * rotatedI + 1.703f * rotatedQ,
                color.W);
        }
    }
}

/// <summary>モノクロ。輝度に色を掛けたものと、元の色を強さで混ぜる</summary>
internal sealed class MonochromeEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Monochrome.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var strength = p.GetFloat("strength") / 100f;
        if (strength <= 0)
        {
            return input;
        }

        var tint = p.GetColor("color");
        PixelOps.Map(input, new Shader(strength, new Vector3(tint.R, tint.G, tint.B) / 255f));
        return input;
    }

    private readonly record struct Shader(float Strength, Vector3 Tint) : IPixelShader
    {
        public void Apply(ref Vector4 color, int x, int y)
        {
            var rgb = new Vector3(color.X, color.Y, color.Z);
            var mono = new Vector3(RenderMath.Luma(color)) * Tint;
            var mixed = Vector3.Lerp(rgb, mono, Strength);
            color = new Vector4(mixed, color.W);
        }
    }
}

/// <summary>
/// クロマキー。色み（YCbCr の CbCr）が抜く色に近い画素を透明にする。
/// 残った画素からは、抜く色の方向の色みを取り除いて、色かぶりを抑える
/// </summary>
internal sealed class ChromaKeyEffect : ICpuEffect
{
    public string Type => BuiltInEffects.ChromaKey.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var key = p.GetColor("color");
        var keyChroma = Chroma(new Vector3(key.R, key.G, key.B) / 255f);
        var length = keyChroma.Length();
        PixelOps.Map(input, new Shader(
            keyChroma,
            length > 0 ? keyChroma / length : Vector2.Zero,
            Tolerance: p.GetFloat("tolerance") / 100f * 0.5f,
            Softness: p.GetFloat("softness") / 100f * 0.5f,
            Spill: p.GetFloat("spill") / 100f));
        return input;
    }

    private static Vector2 Chroma(Vector3 rgb)
    {
        var luma = rgb.X * 0.2126f + rgb.Y * 0.7152f + rgb.Z * 0.0722f;
        return new Vector2((rgb.Z - luma) / 1.8556f, (rgb.X - luma) / 1.5748f);
    }

    private readonly record struct Shader(Vector2 Key, Vector2 KeyDirection, float Tolerance, float Softness, float Spill) : IPixelShader
    {
        public void Apply(ref Vector4 color, int x, int y)
        {
            var rgb = new Vector3(color.X, color.Y, color.Z);
            var chroma = Chroma(rgb);
            var keep = RenderMath.SmoothStep(Tolerance, Tolerance + Softness, Vector2.Distance(chroma, Key));
            var along = Vector2.Dot(chroma, KeyDirection);
            if (along > 0 && Spill > 0)
            {
                // 色み（CbCr）から抜く色の方向の成分を減らし、明るさ（Y）はそのままにして RGB に戻す
                var luma = rgb.X * 0.2126f + rgb.Y * 0.7152f + rgb.Z * 0.0722f;
                chroma -= KeyDirection * along * Spill;
                var r = luma + 1.5748f * chroma.Y;
                var b = luma + 1.8556f * chroma.X;
                var g = (luma - 0.2126f * r - 0.0722f * b) / 0.7152f;
                rgb = new Vector3(r, g, b);
            }

            color = new Vector4(rgb, color.W * keep);
        }
    }
}

/// <summary>カラーキー。RGB の距離が抜く色に近い画素を透明にする</summary>
internal sealed class ColorKeyEffect : ICpuEffect
{
    public string Type => BuiltInEffects.ColorKey.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var key = p.GetColor("color");
        PixelOps.Map(input, new Shader(
            new Vector3(key.R, key.G, key.B) / 255f,
            p.GetFloat("tolerance") / 100f,
            p.GetFloat("softness") / 100f));
        return input;
    }

    private readonly record struct Shader(Vector3 Key, float Tolerance, float Softness) : IPixelShader
    {
        public void Apply(ref Vector4 color, int x, int y)
        {
            // 距離は 0（同じ色）〜 1（白と黒）にそろえる
            var distance = Vector3.Distance(new Vector3(color.X, color.Y, color.Z), Key) / MathF.Sqrt(3);
            color.W *= RenderMath.SmoothStep(Tolerance, Tolerance + Softness, distance);
        }
    }
}

/// <summary>ノイズ。画素ごとの乱数を色に足す。乱数はシードとフレームで決まり、毎回同じになる</summary>
internal sealed class NoiseEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Noise.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var amount = p.GetFloat("amount") / 100f;
        if (amount <= 0)
        {
            return input;
        }

        var seed = unchecked((uint)(long)p.GetNumber("seed") * 0x01000193u ^ (uint)(long)p.GetNumber("frame"));
        PixelOps.Map(input, new Shader(seed, amount, p.GetBool("monochrome"), input.PixelLeft, input.PixelTop));
        return input;
    }

    private readonly record struct Shader(uint Seed, float Amount, bool Monochrome, int Left, int Top) : IPixelShader
    {
        public void Apply(ref Vector4 color, int x, int y)
        {
            // 画像が広がっても同じ場所に同じ乱数が出るよう、ピクセル座標で乱数を決める
            var px = x + Left;
            var py = y + Top;
            var r = RenderMath.Hash(Seed, px, py, 0) * 2 - 1;
            var g = Monochrome ? r : RenderMath.Hash(Seed, px, py, 1) * 2 - 1;
            var b = Monochrome ? r : RenderMath.Hash(Seed, px, py, 2) * 2 - 1;
            color += new Vector4(r, g, b, 0) * Amount;
        }
    }
}

/// <summary>LUT（.cube の 3D LUT）。強さで元の色と混ぜる。ファイルが読めないときは何もしない</summary>
internal sealed class LutEffect : ICpuEffect
{
    public string Type => BuiltInEffects.Lut.Type;

    public EffectImage Apply(EffectImage input, EffectParameters p, EffectContext context)
    {
        var file = p.GetText("file");
        var intensity = p.GetFloat("intensity") / 100f;
        if (string.IsNullOrWhiteSpace(file) || intensity <= 0 || CubeLut.TryLoad(context.Media.ResolvePath(file)) is not { } lut)
        {
            return input;
        }

        PixelOps.Map(input, new Shader(lut, intensity));
        return input;
    }

    private readonly record struct Shader(CubeLut Lut, float Intensity) : IPixelShader
    {
        public void Apply(ref Vector4 color, int x, int y)
        {
            var rgb = new Vector3(color.X, color.Y, color.Z);
            color = new Vector4(Vector3.Lerp(rgb, Lut.Sample(rgb), Intensity), color.W);
        }
    }
}
