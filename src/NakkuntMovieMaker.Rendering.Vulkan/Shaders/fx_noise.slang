// ノイズ（NoiseEffect と同じ）：ピクセル座標（画像が広がっても同じ場所）ごとの乱数を色に足す
import common;
import work;

struct Params
{
    int width;
    int height;
    int left;
    int top;
    uint seed;
    float amount;
    int monochrome;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    uint index = id.y * uint(p.width) + id.x;
    float4 c = nmmUnpackWork(image[index]);
    if (nmmIsTransparent(c))
    {
        return;
    }

    int px = int(id.x) + p.left;
    int py = int(id.y) + p.top;
    float r = nmmHash(p.seed, px, py, 0) * 2.0 - 1.0;
    float g = p.monochrome != 0 ? r : nmmHash(p.seed, px, py, 1) * 2.0 - 1.0;
    float b = p.monochrome != 0 ? r : nmmHash(p.seed, px, py, 2) * 2.0 - 1.0;
    image[index] = nmmPackWork(nmmPremultiply(nmmStraight(c) + float3(r, g, b) * p.amount, c.a));
}
