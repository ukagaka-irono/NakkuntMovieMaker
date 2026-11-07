// テスト用：色を LUT で変換する（CubeLut.Sample と比べる）
import lut;

struct Params
{
    float3 domainMin;
    int size;
    float3 domainScale;
    int count;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<float> table;
[[vk::binding(1, 0)]] StructuredBuffer<float> input;
[[vk::binding(2, 0)]] RWStructuredBuffer<float> output;

[shader("compute")]
[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    int i = int(id.x);
    if (i >= p.count)
    {
        return;
    }

    float3 rgb = float3(input[i * 3], input[i * 3 + 1], input[i * 3 + 2]);
    float3 mapped = nmmSampleLut(table, p.size, p.domainMin, p.domainScale, rgb);
    output[i * 3] = mapped.x;
    output[i * 3 + 1] = mapped.y;
    output[i * 3 + 2] = mapped.z;
}
