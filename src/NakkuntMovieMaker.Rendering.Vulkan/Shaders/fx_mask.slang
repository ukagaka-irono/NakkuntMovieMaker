// マスク（MaskEffect と同じ）：図形（角の丸い四角形か楕円。ローカル座標で、回転できる）の内側だけを残す。
// 境界は、図形までの符号付き距離から求める（ぼかすときは、ガウスぼかしをかけた境界と同じ形）
import work;

struct Params
{
    int width;
    int height;
    int left;
    int top;
    float scale;
    float x;
    float y;
    float cosine;
    float sine;
    float halfWidth;
    float halfHeight;
    float radius;
    int ellipse;
    float sigma;
    int invert;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;

// 誤差関数の近似（Abramowitz と Stegun 7.1.26。誤差は 1.5e-7 以下）
float erfApprox(float x)
{
    float s = sign(x);
    float a = abs(x);
    float t = 1.0 / (1.0 + 0.3275911 * a);
    float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-a * a);
    return s * y;
}

// 図形までの符号付き距離（ローカル座標。内側が負）
float shapeDistance(float2 q)
{
    float2 half = float2(p.halfWidth, p.halfHeight);
    if (p.ellipse != 0)
    {
        float k0 = length(q / half);
        float k1 = length(q / (half * half));
        return k1 > 0.0 ? k0 * (k0 - 1.0) / k1 : -min(half.x, half.y);
    }

    float2 d = abs(q) - (half - p.radius);
    return length(max(d, float2(0.0))) + min(max(d.x, d.y), 0.0) - p.radius;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    float2 local = (float2(float(p.left + int(id.x)), float(p.top + int(id.y))) + 0.5) / p.scale;
    float2 offset = local - float2(p.x, p.y);
    float2 q = float2(p.cosine * offset.x + p.sine * offset.y, -p.sine * offset.x + p.cosine * offset.y);

    float coverage = 0.0;
    if (p.halfWidth > 0.0 && p.halfHeight > 0.0)
    {
        float distance = shapeDistance(q);
        coverage = p.sigma > 0.0
            ? 0.5 * (1.0 - erfApprox(distance / (p.sigma * sqrt(2.0))))
            : clamp(0.5 - distance * p.scale, 0.0, 1.0);
    }

    if (p.invert != 0)
    {
        coverage = 1.0 - coverage;
    }

    uint index = id.y * uint(p.width) + id.x;
    image[index] = nmmPackWork(nmmUnpackWork(image[index]) * coverage);
}
