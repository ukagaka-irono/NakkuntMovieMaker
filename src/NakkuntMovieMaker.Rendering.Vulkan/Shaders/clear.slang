// 作業用の画像を、1 つの色（premultiplied）で塗る
import work;

struct Params
{
    float r;
    float g;
    float b;
    float a;
    int count;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> target;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) < p.count)
    {
        target[id.x] = nmmPackWork(float4(p.r, p.g, p.b, p.a));
    }
}
