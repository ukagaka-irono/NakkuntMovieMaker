// 縁取り（BorderEffect と同じ）：不透明な部分からの距離が太さ以内の部分を、縁の色で塗る（縁は 1 画素でなめらかにする）
import work;

struct Params
{
    int count;
    float width;
    float r;
    float g;
    float b;
    float a;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<float> distance;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) < p.count)
    {
        float coverage = clamp(p.width - sqrt(distance[id.x]) + 0.5, 0.0, 1.0) * p.a;
        image[id.x] = nmmPackWork(float4(float3(p.r, p.g, p.b) * coverage, coverage));
    }
}
