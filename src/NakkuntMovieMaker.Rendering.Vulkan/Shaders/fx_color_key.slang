// カラーキー（ColorKeyEffect と同じ）：RGB の距離が抜く色に近い画素を透明にする
import common;
import work;

struct Params
{
    int count;
    float keyR;
    float keyG;
    float keyB;
    float tolerance;
    float softness;
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
    float d = distance(rgb, float3(p.keyR, p.keyG, p.keyB)) / sqrt(3.0);
    image[id.x] = nmmPackWork(nmmPremultiply(rgb, c.a * nmmSmoothStep(p.tolerance, p.tolerance + p.softness, d)));
}
