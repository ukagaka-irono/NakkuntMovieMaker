// 描画の方式（CPU・GPU）に共通の計算。RenderMath（C#）と同じ式・同じ定数で計算する。変えるときは両方を変え、テストで一致を確かめる
module common;

// 0 以上 1 未満の、(seed, x, y, channel) から決まる乱数（RenderMath.Hash と同じ。整数の演算だけなので、ビット単位で同じになる）
public float nmmHash(uint seed, int x, int y, int channel)
{
    uint h = seed * 0x9E3779B1u ^ uint(x) * 0x85EBCA77u ^ uint(y) * 0xC2B2AE3Du ^ uint(channel) * 0x27D4EB2Fu;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 12;
    h *= 0x297A2D39u;
    h ^= h >> 15;
    return float(h >> 8) * (1.0 / 16777216.0);
}

// BT.709 の輝度（RenderMath.Luma と同じ）
public float nmmLuma(float3 color)
{
    return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722;
}

// RenderMath.SmoothStep と同じ（幅が 0 なら段差）
public float nmmSmoothStep(float edge0, float edge1, float x)
{
    if (edge1 <= edge0)
    {
        return x < edge0 ? 0.0 : 1.0;
    }

    float t = clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}
