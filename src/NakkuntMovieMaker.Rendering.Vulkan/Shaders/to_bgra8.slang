// 作業用の画像（premultiplied・16 ビット浮動小数点）を、読み戻すための premultiplied の BGRA（8 ビット）にする（0〜1 に収めて、四捨五入）。
// 出力の変換（ColorLut：シェーパーと 3D LUT）があれば、sRGB にしてからかける
import work;
import colorlut;

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
        c = float4(nmmApplyColorLut(lut, p.shaperLength, p.cubeSize, p.cubeOffset, p.lutEncodeSrgb, min(c.rgb, c.a) / c.a) * c.a, c.a);
    }

    output[index] = unorm8(c.b) | (unorm8(c.g) << 8) | (unorm8(c.r) << 16) | (unorm8(c.a) << 24);
}
