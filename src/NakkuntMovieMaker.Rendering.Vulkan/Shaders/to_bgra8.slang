// 作業用の画像（RGBA16F・premultiplied）を、読み戻すための premultiplied の BGRA（8 ビット）にする（0〜1 に収めて、四捨五入）
struct Params
{
    int width;
    int height;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] [format("rgba16f")] RWTexture2D<float4> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> output;

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

    float4 c = source[int2(id.xy)];
    output[id.y * p.width + id.x] = unorm8(c.b) | (unorm8(c.g) << 8) | (unorm8(c.r) << 16) | (unorm8(c.a) << 24);
}
