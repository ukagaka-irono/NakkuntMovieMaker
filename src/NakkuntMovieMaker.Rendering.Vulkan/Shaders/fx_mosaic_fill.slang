// モザイクの 2 段目：画素を、その画素のブロックの平均の色にする（どのブロックにも入らない画素はそのまま）
import work;

struct Params
{
    int width;
    int height;
    int columns;
    int indexX;
    int indexY;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;
[[vk::binding(1, 0)]] StructuredBuffer<int> table;
[[vk::binding(2, 0)]] StructuredBuffer<float4> averages;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    int column = table[p.indexX + int(id.x)];
    int row = table[p.indexY + int(id.y)];
    if (column >= 0 && row >= 0)
    {
        image[id.y * uint(p.width) + id.x] = nmmPackWork(averages[row * p.columns + column]);
    }
}
