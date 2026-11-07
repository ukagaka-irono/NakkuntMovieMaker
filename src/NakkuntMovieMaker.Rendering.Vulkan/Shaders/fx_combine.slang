// 2 つの作業用の画像を、ピクセル座標を合わせて重ねる（target を書き換える。source の外は透明）
// mode：0 は source を上に（SrcOver）、1 は source を下に（DstOver）、2 はスクリーン（source に factor を掛ける）、3 は source を写す
import work;

struct Params
{
    int targetLeft;
    int targetTop;
    int targetWidth;
    int targetHeight;
    int sourceLeft;
    int sourceTop;
    int sourceWidth;
    int sourceHeight;
    int mode;
    float factor;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<uint2> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint2> target;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.targetWidth || int(id.y) >= p.targetHeight)
    {
        return;
    }

    int sx = p.targetLeft + int(id.x) - p.sourceLeft;
    int sy = p.targetTop + int(id.y) - p.sourceTop;
    float4 s = float4(0.0);
    if (sx >= 0 && sy >= 0 && sx < p.sourceWidth && sy < p.sourceHeight)
    {
        s = nmmUnpackWork(source[sy * p.sourceWidth + sx]);
    }

    uint index = id.y * uint(p.targetWidth) + id.x;
    if (p.mode == 3)
    {
        target[index] = nmmPackWork(s);
        return;
    }

    float4 d = nmmUnpackWork(target[index]);
    float4 result;
    if (p.mode == 0)
    {
        result = s + d * (1.0 - s.a);
    }
    else if (p.mode == 1)
    {
        result = d + s * (1.0 - d.a);
    }
    else
    {
        s *= p.factor;
        result = s + d - s * d;
    }

    target[index] = nmmPackWork(result);
}
