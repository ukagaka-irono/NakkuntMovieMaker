// ぼかしの 1 方向（横か縦）。出力の画素 P の値は、入力の (P − shift) のまわりを重み（weights。CPU で求める）で平均したもの。
// 入力の外は透明（Skia の Decal と同じ）。shift の端数は線形に補間する（ドロップシャドウのずれ）
import work;

struct Params
{
    int sourceLeft;
    int sourceTop;
    int sourceWidth;
    int sourceHeight;
    int targetLeft;
    int targetTop;
    int targetWidth;
    int targetHeight;
    int horizontal;
    int radius;
    float shift;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<uint2> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint2> target;
[[vk::binding(2, 0)]] StructuredBuffer<float> weights;

float4 texel(int x, int y)
{
    if (x < 0 || y < 0 || x >= p.sourceWidth || y >= p.sourceHeight)
    {
        return float4(0.0);
    }

    return nmmUnpackWork(source[y * p.sourceWidth + x]);
}

float4 along(int position, int across)
{
    return p.horizontal != 0 ? texel(position, across) : texel(across, position);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.targetWidth || int(id.y) >= p.targetHeight)
    {
        return;
    }

    // 入力の画像の中での位置（画素の番号）
    int sx = p.targetLeft + int(id.x) - p.sourceLeft;
    int sy = p.targetTop + int(id.y) - p.sourceTop;
    int position = p.horizontal != 0 ? sx : sy;
    int across = p.horizontal != 0 ? sy : sx;

    float4 sum = float4(0.0);
    for (int k = -p.radius; k <= p.radius; k++)
    {
        float weight = weights[k + p.radius];
        float at = float(position - k) - p.shift;
        float base = floor(at);
        float fraction = at - base;
        float4 value = along(int(base), across);
        if (fraction > 0.0)
        {
            value = lerp(value, along(int(base) + 1, across), fraction);
        }

        sum += value * weight;
    }

    target[id.y * uint(p.targetWidth) + id.x] = nmmPackWork(sum);
}
