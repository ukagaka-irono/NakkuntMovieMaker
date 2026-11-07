// .cube の 3D LUT を引く（CubeLut.Sample と同じ三重線形補間。ハードウェアの補間は使わない）。
// table は格子の色（赤が一番速く変わる順）を float で 3 つずつ並べたもの
module lut;

float3 lutAt(StructuredBuffer<float> table, int size, int r, int g, int b)
{
    int i = ((b * size + g) * size + r) * 3;
    return float3(table[i], table[i + 1], table[i + 2]);
}

public float3 nmmSampleLut(StructuredBuffer<float> table, int size, float3 domainMin, float3 domainScale, float3 rgb)
{
    float3 position = clamp((rgb - domainMin) * domainScale, 0.0, 1.0) * float(size - 1);
    int r0 = min(int(position.x), size - 2);
    int g0 = min(int(position.y), size - 2);
    int b0 = min(int(position.z), size - 2);
    float3 f = position - float3(r0, g0, b0);
    float3 c00 = lerp(lutAt(table, size, r0, g0, b0), lutAt(table, size, r0 + 1, g0, b0), f.x);
    float3 c10 = lerp(lutAt(table, size, r0, g0 + 1, b0), lutAt(table, size, r0 + 1, g0 + 1, b0), f.x);
    float3 c01 = lerp(lutAt(table, size, r0, g0, b0 + 1), lutAt(table, size, r0 + 1, g0, b0 + 1), f.x);
    float3 c11 = lerp(lutAt(table, size, r0, g0 + 1, b0 + 1), lutAt(table, size, r0 + 1, g0 + 1, b0 + 1), f.x);
    return lerp(lerp(c00, c10, f.y), lerp(c01, c11, f.y), f.z);
}
