// 色の変換（ColorLut：シェーパーと 3D LUT）を引く。ColorLut.Sample と同じ順番・同じ補間。
// lut の先頭の shaperLength 個がシェーパー（√x を等間隔に並べた表）、cubeOffset からが 1 辺 cubeSize の 3D LUT（赤が一番速く変わる順）
module colorlut;

float shape(StructuredBuffer<float> lut, int shaperLength, float x)
{
    float position = sqrt(clamp(x, 0.0, 1.0)) * float(shaperLength - 1);
    int index = min(int(position), shaperLength - 2);
    float fraction = position - float(index);
    return lerp(lut[index], lut[index + 1], fraction);
}

float3 cubeAt(StructuredBuffer<float> lut, int cubeSize, int cubeOffset, int r, int g, int b)
{
    int i = cubeOffset + ((b * cubeSize + g) * cubeSize + r) * 3;
    return float3(lut[i], lut[i + 1], lut[i + 2]);
}

float3 sampleCube(StructuredBuffer<float> lut, int size, int offset, float3 rgb)
{
    float3 position = clamp(rgb, 0.0, 1.0) * float(size - 1);
    int r0 = min(int(position.x), size - 2);
    int g0 = min(int(position.y), size - 2);
    int b0 = min(int(position.z), size - 2);
    float3 f = position - float3(r0, g0, b0);
    float3 c00 = lerp(cubeAt(lut, size, offset, r0, g0, b0), cubeAt(lut, size, offset, r0 + 1, g0, b0), f.x);
    float3 c10 = lerp(cubeAt(lut, size, offset, r0, g0 + 1, b0), cubeAt(lut, size, offset, r0 + 1, g0 + 1, b0), f.x);
    float3 c01 = lerp(cubeAt(lut, size, offset, r0, g0, b0 + 1), cubeAt(lut, size, offset, r0 + 1, g0, b0 + 1), f.x);
    float3 c11 = lerp(cubeAt(lut, size, offset, r0, g0 + 1, b0 + 1), cubeAt(lut, size, offset, r0 + 1, g0 + 1, b0 + 1), f.x);
    return lerp(lerp(c00, c10, f.y), lerp(c01, c11, f.y), f.z);
}

float encodeSrgb(float value)
{
    return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(value, 1.0 / 2.4) - 0.055;
}

// straight の色（0〜1）を変換し、0〜1 に収める
public float3 nmmApplyColorLut(StructuredBuffer<float> lut, int shaperLength, int cubeSize, int cubeOffset, int lutEncodeSrgb, float3 rgb)
{
    float3 u = float3(shape(lut, shaperLength, rgb.r), shape(lut, shaperLength, rgb.g), shape(lut, shaperLength, rgb.b));
    float3 value = sampleCube(lut, cubeSize, cubeOffset, u);
    if (lutEncodeSrgb != 0)
    {
        value = float3(encodeSrgb(value.r), encodeSrgb(value.g), encodeSrgb(value.b));
    }

    return clamp(value, 0.0, 1.0);
}
