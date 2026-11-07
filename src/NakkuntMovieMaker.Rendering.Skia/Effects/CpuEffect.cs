using System.Collections.Frozen;
using System.Numerics;
using NakkuntMovieMaker.Rendering.Effects;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

/// <summary>エフェクトを処理するときに使えるもの</summary>
internal sealed record EffectContext(IMediaSource Media);

/// <summary>
/// エフェクトの CPU での処理。input は書き換えてよく、そのまま返してもよい。
/// 新しい画像を返したときは、呼び出し側が input を破棄する
/// </summary>
internal interface ICpuEffect
{
    string Type { get; }

    EffectImage Apply(EffectImage input, EffectParameters parameters, EffectContext context);
}

/// <summary>組み込みのエフェクトの CPU での処理の一覧</summary>
internal static class CpuEffects
{
    private static readonly FrozenDictionary<string, ICpuEffect> ByType = new ICpuEffect[]
    {
        new ColorAdjustEffect(), new LutEffect(), new BlurEffect(), new MosaicEffect(), new SharpenEffect(),
        new ChromaKeyEffect(), new ColorKeyEffect(), new DropShadowEffect(), new BorderEffect(), new GlowEffect(),
        new MaskEffect(), new FadeEffect(), new NoiseEffect(), new ShakeEffect(), new MonochromeEffect(),
    }.ToFrozenDictionary(effect => effect.Type, StringComparer.Ordinal);

    public static ICpuEffect? Find(string type) => ByType.GetValueOrDefault(type);
}

/// <summary>1 画素ごとの色の処理。色は straight alpha の 0〜1（X=R, Y=G, Z=B, W=A）。(x, y) はビットマップの中の位置</summary>
internal interface IPixelShader
{
    void Apply(ref Vector4 color, int x, int y);
}

internal static unsafe class PixelOps
{
    /// <summary>
    /// すべての画素に shader をかける（行ごとに並列に処理する）。完全に透明な画素は、色がないので飛ばす
    /// </summary>
    public static void Map<T>(EffectImage image, T shader)
        where T : struct, IPixelShader
    {
        var pixels = image.Pixels;
        var rowBytes = image.RowBytes;
        var width = image.Width;
        Parallel.For(0, image.Height, y =>
        {
            var row = pixels + y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                var a = p[3];
                if (a == 0)
                {
                    continue;
                }

                var inverse = 1f / a;
                var color = new Vector4(p[2] * inverse, p[1] * inverse, p[0] * inverse, a / 255f);
                shader.Apply(ref color, x, y);
                color = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
                var alpha = color.W * 255f;
                p[0] = (byte)(color.Z * alpha + 0.5f);
                p[1] = (byte)(color.Y * alpha + 0.5f);
                p[2] = (byte)(color.X * alpha + 0.5f);
                p[3] = (byte)(alpha + 0.5f);
            }
        });
    }

    /// <summary>不透明度に factor を掛ける（premultiplied なので、すべてのチャンネルに掛ける）</summary>
    public static void MultiplyAlpha(EffectImage image, float factor)
    {
        var pixels = image.Pixels;
        var rowBytes = image.RowBytes;
        var length = image.Width * 4;
        Parallel.For(0, image.Height, y =>
        {
            var row = pixels + y * rowBytes;
            for (var i = 0; i < length; i++)
            {
                row[i] = (byte)(row[i] * factor + 0.5f);
            }
        });
    }
}
