// 縁取りの距離変換の 2 段目：行ごとに、1 段目の結果から 2 次元の距離の 2 乗を求める
struct Params
{
    int width;
    int height;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<float> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<float> grid;
[[vk::binding(2, 0)]] RWStructuredBuffer<int> v;
[[vk::binding(3, 0)]] RWStructuredBuffer<float> z;

static int row;

float f(int q) { return source[row * p.width + q]; }
int getV(int k) { return v[row * p.width + k]; }
void setV(int k, int value) { v[row * p.width + k] = value; }
float getZ(int k) { return z[row * (p.width + 1) + k]; }
void setZ(int k, float value) { z[row * (p.width + 1) + k] = value; }
void output(int q, float value) { grid[row * p.width + q] = value; }

// 1 次元のユークリッド距離変換（Felzenszwalb と Huttenlocher の方法。DistanceTransform と同じ）
float intersection(int q, int r)
{
    return (f(q) + float(q) * float(q) - (f(r) + float(r) * float(r))) / (2.0 * float(q) - 2.0 * float(r));
}

void transform(int n)
{
    int k = 0;
    setV(0, 0);
    setZ(0, asfloat(0xFF800000u));
    setZ(1, asfloat(0x7F800000u));
    for (int q = 1; q < n; q++)
    {
        float s = intersection(q, getV(k));
        while (s <= getZ(k))
        {
            k--;
            s = intersection(q, getV(k));
        }

        k++;
        setV(k, q);
        setZ(k, s);
        setZ(k + 1, asfloat(0x7F800000u));
    }

    k = 0;
    for (int q = 0; q < n; q++)
    {
        while (getZ(k + 1) < float(q))
        {
            k++;
        }

        int delta = q - getV(k);
        output(q, float(delta * delta) + f(getV(k)));
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) < p.height)
    {
        row = int(id.x);
        transform(p.width);
    }
}
