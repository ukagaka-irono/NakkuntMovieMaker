// モザイク（MosaicEffect と同じ）の 1 段目：ブロックごとの色の平均。ブロックの範囲は CPU で求めて表にする
import work;

struct Params
{
    int width;
    int columns;
    int rows;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<uint2> image;
[[vk::binding(1, 0)]] StructuredBuffer<int> table;
[[vk::binding(2, 0)]] RWStructuredBuffer<float4> averages;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.columns || int(id.y) >= p.rows)
    {
        return;
    }

    // 表：列の始まり・列の終わり・行の始まり・行の終わり
    int left = table[id.x];
    int right = table[p.columns + id.x];
    int top = table[2 * p.columns + id.y];
    int bottom = table[2 * p.columns + p.rows + id.y];
    float4 sum = float4(0.0);
    for (int y = top; y < bottom; y++)
    {
        for (int x = left; x < right; x++)
        {
            sum += nmmUnpackWork(image[y * p.width + x]);
        }
    }

    int count = max(1, (right - left) * (bottom - top));
    averages[id.y * uint(p.columns) + id.x] = sum / float(count);
}
