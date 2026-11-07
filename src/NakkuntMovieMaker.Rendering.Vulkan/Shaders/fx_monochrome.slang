// モノクロ（MonochromeEffect と同じ）：輝度に色を掛けたものと、元の色を強さで混ぜる
import common;
import work;

struct Params
{
    int count;
    float strength;
    float tintR;
    float tintG;
    float tintB;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.count)
    {
        return;
    }

    float4 c = nmmUnpackWork(image[id.x]);
    if (nmmIsTransparent(c))
    {
        return;
    }

    float3 rgb = nmmStraight(c);
    float3 mono = nmmLuma(rgb) * float3(p.tintR, p.tintG, p.tintB);
    image[id.x] = nmmPackWork(nmmPremultiply(lerp(rgb, mono, p.strength), c.a));
}
