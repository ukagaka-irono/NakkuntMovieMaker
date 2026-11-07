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

// 合成モード（Core の BlendMode と同じ番号）。色は premultiplied。Skia（CPU 版）の式と同じにする
public static const int BlendNormal = 0;
public static const int BlendAdd = 1;
public static const int BlendSubtract = 2;
public static const int BlendMultiply = 3;
public static const int BlendScreen = 4;
public static const int BlendOverlay = 5;
public static const int BlendLighten = 6;
public static const int BlendDarken = 7;
public static const int BlendDifference = 8;

float overlayChannel(float s, float d, float sa, float da)
{
    float blended = 2.0 * d <= da ? 2.0 * s * d : sa * da - 2.0 * (da - d) * (sa - s);
    return blended + s * (1.0 - da) + d * (1.0 - sa);
}

// s（重ねる色）を d（下の色）に重ねた結果
public float4 nmmBlend(int mode, float4 s, float4 d)
{
    float sa = s.a;
    float da = d.a;
    float alpha = sa + da - sa * da;
    switch (mode)
    {
    case BlendAdd:
        return min(s + d, 1.0);
    case BlendSubtract:
        // SkiaCpuBackend の減算（SkSL のブレンダー）と同じ
        return float4(max(d.rgb - s.rgb, 0.0), sa + da * (1.0 - sa));
    case BlendMultiply:
        return float4(s.rgb * (1.0 - da) + d.rgb * (1.0 - sa) + s.rgb * d.rgb, alpha);
    case BlendScreen:
        return float4(s.rgb + d.rgb - s.rgb * d.rgb, alpha);
    case BlendOverlay:
        return float4(overlayChannel(s.r, d.r, sa, da), overlayChannel(s.g, d.g, sa, da), overlayChannel(s.b, d.b, sa, da), alpha);
    case BlendLighten:
        return float4(s.rgb + d.rgb - min(s.rgb * da, d.rgb * sa), alpha);
    case BlendDarken:
        return float4(s.rgb + d.rgb - max(s.rgb * da, d.rgb * sa), alpha);
    case BlendDifference:
        return float4(s.rgb + d.rgb - 2.0 * min(s.rgb * da, d.rgb * sa), alpha);
    default:
        return s + d * (1.0 - sa);
    }
}
