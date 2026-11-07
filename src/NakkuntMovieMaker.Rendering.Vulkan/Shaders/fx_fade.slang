// フェード（FadeEffect と同じ）：不透明度に係数を掛ける（premultiplied なので、すべてのチャンネルに掛ける）
import work;

struct Params
{
    int count;
    float factor;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) < p.count)
    {
        image[id.x] = nmmPackWork(nmmUnpackWork(image[id.x]) * p.factor);
    }
}
