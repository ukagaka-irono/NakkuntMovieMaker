// シャープ（SharpenEffect と同じ）：元の色に「元の色 − ぼかした色」を強さだけ足す（色は不透明度を超えない）
import work;

struct Params
{
    int width;
    int height;
    int offsetX;
    int offsetY;
    int blurredWidth;
    float amount;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;
[[vk::binding(1, 0)]] StructuredBuffer<uint2> blurred;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    uint index = id.y * uint(p.width) + id.x;
    float4 s = nmmUnpackWork(image[index]);
    float4 b = nmmUnpackWork(blurred[(int(id.y) + p.offsetY) * p.blurredWidth + int(id.x) + p.offsetX]);
    float3 rgb = clamp(s.rgb + p.amount * (s.rgb - b.rgb), 0.0, s.a);
    image[index] = nmmPackWork(float4(rgb, s.a));
}
