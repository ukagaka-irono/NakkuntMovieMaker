using System.Numerics;

namespace NakkuntMovieMaker.Rendering;

/// <summary>
/// 描画の方式（CPU・GPU）に共通の計算。GPU のシェーダーは、ここと同じ式・同じ定数で計算し、CPU 版と同じ結果になるようにする
/// （シェーダーを変えるときは、ここも合わせて変え、テストで一致を確かめる）
/// </summary>
public static class RenderMath
{
    /// <summary>BT.709 の輝度（色は 0〜1。X=R, Y=G, Z=B）</summary>
    public static float Luma(Vector4 color) => color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

    /// <summary>0 以上 1 未満の、(seed, x, y, channel) から決まる乱数（整数の演算だけなので、GPU でもビット単位で同じになる）</summary>
    public static float Hash(uint seed, int x, int y, int channel)
    {
        var h = seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du ^ (uint)channel * 0x27D4EB2Fu;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24));
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0 : 1;
        }

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>ぼかしの半径（シーンの画素）と描く倍率から、ガウスぼかしの標準偏差（出力の画素）</summary>
    public static float BlurSigma(double radius, float scale) => (float)(radius * scale / 3);

    /// <summary>標準偏差 sigma のぼかしで、画像の外側に広がる幅（画素）</summary>
    public static int BlurMargin(float sigma) => sigma <= 0 ? 0 : (int)Math.Ceiling(sigma * 3);

    /// <summary>トランジションの向き（後のアイテムが入ってくる側）を、出力の上での単位ベクトルにする</summary>
    public static Vector2 TransitionDirection(string direction) => direction switch
    {
        "fromRight" => new Vector2(1, 0),
        "fromTop" => new Vector2(0, -1),
        "fromBottom" => new Vector2(0, 1),
        _ => new Vector2(-1, 0),
    };
}
