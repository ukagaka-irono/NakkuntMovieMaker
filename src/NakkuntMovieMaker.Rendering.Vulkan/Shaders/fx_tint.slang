// 色で塗る（Skia の SrcIn）：色（straight）を、画素の不透明度の形で塗る
import work;

struct Params
{
    int count;
    float r;
    float g;
    float b;
    float a;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) < p.count)
    {
        float coverage = nmmUnpackWork(image[id.x]).a * p.a;
        image[id.x] = nmmPackWork(float4(float3(p.r, p.g, p.b) * coverage, coverage));
    }
}
