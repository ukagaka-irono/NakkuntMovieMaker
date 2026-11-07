// 作業用の画像（premultiplied・16 ビット浮動小数点）を、読み戻すための premultiplied の BGRA（8 ビット）にする（0〜1 に収めて、四捨五入）。
// 出力の変換（ColorLut：シェーパーと 3D LUT）があれば、sRGB にしてからかける
import work;

struct Params
{
    int width;
    int height;

    // 1 なら、作業用の画像（リニア）を sRGB にしてから 8 ビットにする
    int encodeSrgb;

    // 出力の変換：useLut が 1 なら、lut の先頭の shaperLength 個がシェーパー、cubeOffset からが 1 辺 cubeSize の 3D LUT
    int useLut;
    int shaperLength;
    int cubeSize;
    int cubeOffset;
    int lutEncodeSrgb;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> output;
[[vk::binding(2, 0)]] StructuredBuffer<float> lut;

// ColorLut.Shape と同じ：√x を等間隔に並べた表を線形に補間する
float shape(float x)
{
    float position = sqrt(clamp(x, 0.0, 1.0)) * float(p.shaperLength - 1);
    int index = min(int(position), p.shaperLength - 2);
    float fraction = position - float(index);
    return lerp(lut[index], lut[index + 1], fraction);
}

float3 cubeAt(int r, int g, int b)
{
    int i = p.cubeOffset + ((b * p.cubeSize + g) * p.cubeSize + r) * 3;
    return float3(lut[i], lut[i + 1], lut[i + 2]);
}

// CubeLut.Sample と同じ三重線形補間（入力は 0〜1）
float3 sampleCube(float3 rgb)
{
    int size = p.cubeSize;
    float3 position = clamp(rgb, 0.0, 1.0) * float(size - 1);
    int r0 = min(int(position.x), size - 2);
    int g0 = min(int(position.y), size - 2);
    int b0 = min(int(position.z), size - 2);
    float3 f = position - float3(r0, g0, b0);
    float3 c00 = lerp(cubeAt(r0, g0, b0), cubeAt(r0 + 1, g0, b0), f.x);
    float3 c10 = lerp(cubeAt(r0, g0 + 1, b0), cubeAt(r0 + 1, g0 + 1, b0), f.x);
    float3 c01 = lerp(cubeAt(r0, g0, b0 + 1), cubeAt(r0 + 1, g0, b0 + 1), f.x);
    float3 c11 = lerp(cubeAt(r0, g0 + 1, b0 + 1), cubeAt(r0 + 1, g0 + 1, b0 + 1), f.x);
    return lerp(lerp(c00, c10, f.y), lerp(c01, c11, f.y), f.z);
}

// ColorLut.Sample と同じ（straight の色を変換し、0〜1 に収める）
float3 applyLut(float3 rgb)
{
    float3 value = sampleCube(float3(shape(rgb.r), shape(rgb.g), shape(rgb.b)));
    if (p.lutEncodeSrgb != 0)
    {
        value = float3(
            value.r <= 0.0031308 ? value.r * 12.92 : 1.055 * pow(value.r, 1.0 / 2.4) - 0.055,
            value.g <= 0.0031308 ? value.g * 12.92 : 1.055 * pow(value.g, 1.0 / 2.4) - 0.055,
            value.b <= 0.0031308 ? value.b * 12.92 : 1.055 * pow(value.b, 1.0 / 2.4) - 0.055);
    }

    return clamp(value, 0.0, 1.0);
}

uint unorm8(float value)
{
    return uint(floor(clamp(value, 0.0, 1.0) * 255.0 + 0.5));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    uint index = id.y * p.width + id.x;
    float4 c = nmmUnpackWork(source[index]);
    if (p.encodeSrgb != 0)
    {
        c = clamp(c, 0.0, 1.0);
        c = nmmPremultipliedToSrgb(float4(min(c.rgb, c.a), c.a));
    }

    if (p.useLut != 0 && c.a > 0.0)
    {
        c = clamp(c, 0.0, 1.0);
        c = float4(applyLut(min(c.rgb, c.a) / c.a) * c.a, c.a);
    }

    output[index] = unorm8(c.b) | (unorm8(c.g) << 8) | (unorm8(c.r) << 16) | (unorm8(c.a) << 24);
}
