// premultiplied の BGRA（8 ビット）を、作業用の画像（16 ビット浮動小数点）にする
import work;

struct Params
{
    int count;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<uint> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) < p.count)
    {
        uint bgra = source[id.x];
        float4 c = float4(float((bgra >> 16) & 255u), float((bgra >> 8) & 255u), float(bgra & 255u), float(bgra >> 24)) / 255.0;
        image[id.x] = nmmPackWork(c);
    }
}
