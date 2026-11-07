// テスト用：ノイズのハッシュを格子の位置ごとに計算する（RenderMath.Hash と比べる）
import common;

struct Params
{
    uint seed;
    int originX;
    int originY;
    int width;
    int height;
    int channel;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<float> output;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    output[id.y * p.width + id.x] = nmmHash(p.seed, p.originX + int(id.x), p.originY + int(id.y), p.channel);
}
