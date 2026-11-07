// LUT（LutEffect と同じ）：.cube の 3D LUT で変換し、強さで元の色と混ぜる
import lut;
import work;

struct Params
{
    int count;
    int size;
    float intensity;
    float minR;
    float minG;
    float minB;
    float scaleR;
    float scaleG;
    float scaleB;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;
[[vk::binding(1, 0)]] StructuredBuffer<float> table;

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
    float3 mapped = nmmSampleLut(table, p.size, float3(p.minR, p.minG, p.minB), float3(p.scaleR, p.scaleG, p.scaleB), rgb);
    image[id.x] = nmmPackWork(nmmPremultiply(lerp(rgb, mapped, p.intensity), c.a));
}
