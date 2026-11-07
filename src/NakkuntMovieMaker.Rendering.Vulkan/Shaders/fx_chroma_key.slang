// クロマキー（ChromaKeyEffect と同じ）：色み（CbCr）が抜く色に近い画素を透明にし、残った画素の色かぶりを抑える
import common;
import work;

struct Params
{
    int count;
    float keyX;
    float keyY;
    float directionX;
    float directionY;
    float tolerance;
    float softness;
    float spill;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> image;

[shader("compute")]
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.count)
    {
        return;
    }

    float4 c = nmmUnpackWork(image[id.x]);
    if (nmmIsTransparent(c))
    {
        return;
    }

    float3 rgb = nmmStraight(c);
    float2 key = float2(p.keyX, p.keyY);
    float2 direction = float2(p.directionX, p.directionY);
    float luma = rgb.r * 0.2126 + rgb.g * 0.7152 + rgb.b * 0.0722;
    float2 cbcr = float2((rgb.b - luma) / 1.8556, (rgb.r - luma) / 1.5748);
    float keep = nmmSmoothStep(p.tolerance, p.tolerance + p.softness, distance(cbcr, key));
    float along = dot(cbcr, direction);
    if (along > 0.0 && p.spill > 0.0)
    {
        cbcr -= direction * along * p.spill;
        float r = luma + 1.5748 * cbcr.y;
        float b = luma + 1.8556 * cbcr.x;
        float g = (luma - 0.2126 * r - 0.0722 * b) / 0.7152;
        rgb = float3(r, g, b);
    }

    image[id.x] = nmmPackWork(nmmPremultiply(rgb, c.a * keep));
}
