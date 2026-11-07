// 作業用の画像（premultiplied の RGBA を 16 ビット浮動小数点で持つ）。どの GPU でも使えるように、画像ではなくバッファーに入れる。
// 1 画素は uint2（RG と BA を 16 ビットずつ）。16 ビット浮動小数点への変換は、整数の演算だけで行う
// （f32tof16 などは SPIR-V の Float16・Int16 の機能を使い、それに対応していない GPU では動かないため）
module work;

// 32 ビットの浮動小数点 → 16 ビットの浮動小数点のビット（最近接の偶数に丸める。NaN は考えない）
uint floatToHalf(float value)
{
    uint bits = asuint(value);
    uint sign = (bits >> 16) & 0x8000u;
    int exponent = int((bits >> 23) & 255u) - 127 + 15;
    uint mantissa = bits & 0x7FFFFFu;
    if (exponent >= 31)
    {
        return sign | 0x7C00u;
    }

    if (exponent <= 0)
    {
        // 非正規化数か 0
        if (exponent < -10)
        {
            return sign;
        }

        mantissa |= 0x800000u;
        uint shift = uint(14 - exponent);
        uint half = mantissa >> shift;
        uint rest = mantissa & ((1u << shift) - 1u);
        uint halfway = 1u << (shift - 1u);
        if (rest > halfway || (rest == halfway && (half & 1u) != 0u))
        {
            half += 1u;
        }

        return sign | half;
    }

    uint result = sign | (uint(exponent) << 10) | (mantissa >> 13);
    uint remainder = mantissa & 0x1FFFu;
    if (remainder > 0x1000u || (remainder == 0x1000u && (result & 1u) != 0u))
    {
        result += 1u; // 繰り上がりは指数にそのまま入る
    }

    return result;
}

// 16 ビットの浮動小数点のビット → 32 ビットの浮動小数点
float halfToFloat(uint half)
{
    uint sign = (half & 0x8000u) << 16;
    uint exponent = (half >> 10) & 31u;
    uint mantissa = half & 1023u;
    if (exponent == 0u)
    {
        float subnormal = float(mantissa) * (1.0 / 16777216.0);
        return sign != 0u ? -subnormal : subnormal;
    }

    if (exponent == 31u)
    {
        return asfloat(sign | 0x7F800000u | (mantissa << 13));
    }

    return asfloat(sign | ((exponent + 112u) << 23) | (mantissa << 13));
}

public float4 nmmUnpackWork(uint2 packed)
{
    return float4(halfToFloat(packed.x & 0xFFFFu), halfToFloat(packed.x >> 16), halfToFloat(packed.y & 0xFFFFu), halfToFloat(packed.y >> 16));
}

public uint2 nmmPackWork(float4 color)
{
    return uint2(floatToHalf(color.r) | (floatToHalf(color.g) << 16), floatToHalf(color.b) | (floatToHalf(color.a) << 16));
}

// エフェクトの「1 画素ずつの色の処理」の決まり（CPU 版の PixelOps.Map と同じ）：
// 完全に透明な画素は飛ばし、色は straight alpha（0〜1）で処理し、最後に 0〜1 に収めて premultiplied に戻す
public bool nmmIsTransparent(float4 premultiplied)
{
    return premultiplied.a <= 0.0;
}

public float3 nmmStraight(float4 premultiplied)
{
    return premultiplied.rgb / premultiplied.a;
}

public float4 nmmPremultiply(float3 rgb, float alpha)
{
    float4 c = clamp(float4(rgb, alpha), 0.0, 1.0);
    return float4(c.rgb * c.a, c.a);
}
