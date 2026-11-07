// 作業用の画像（premultiplied の RGBA を 16 ビット浮動小数点で持つ）。どの GPU でも使えるように、画像ではなくバッファーに入れる
// （MoltenVK の一部の GPU では、読み書きする画像を使えないため）。1 画素は uint2（RG と BA を 16 ビットずつ）
module work;

public float4 nmmUnpackWork(uint2 packed)
{
    return float4(f16tof32(packed.x & 0xFFFFu), f16tof32(packed.x >> 16), f16tof32(packed.y & 0xFFFFu), f16tof32(packed.y >> 16));
}

public uint2 nmmPackWork(float4 color)
{
    return uint2(f32tof16(color.r) | (f32tof16(color.g) << 16), f32tof16(color.b) | (f32tof16(color.a) << 16));
}
