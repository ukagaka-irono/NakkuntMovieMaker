// 色調補正（ColorAdjustEffect と同じ）：ガンマ → 明るさ → コントラスト → 色温度 → 彩度 → 色相
import work;

struct Params
{
    int count;
    float inverseGamma;
    float brightness;
    float contrast;
    float temperature;
    float saturation;
    float hueCos;
    float hueSin;
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
    if (p.inverseGamma != 1.0)
    {
        rgb = pow(max(rgb, 0.0), p.inverseGamma);
    }

    rgb += p.brightness;
    rgb = (rgb - 0.5) * p.contrast + 0.5;
    rgb *= float3(1.0 + p.temperature * 0.2, 1.0, 1.0 - p.temperature * 0.2);

    // 彩度と色相は YIQ で扱う
    float luma = rgb.r * 0.299 + rgb.g * 0.587 + rgb.b * 0.114;
    float i = (rgb.r * 0.596 - rgb.g * 0.274 - rgb.b * 0.322) * p.saturation;
    float q = (rgb.r * 0.211 - rgb.g * 0.523 + rgb.b * 0.312) * p.saturation;
    float ri = i * p.hueCos - q * p.hueSin;
    float rq = i * p.hueSin + q * p.hueCos;
    float3 result = float3(luma + 0.956 * ri + 0.621 * rq, luma - 0.272 * ri - 0.647 * rq, luma - 1.106 * ri + 1.703 * rq);
    image[id.x] = nmmPackWork(nmmPremultiply(result, c.a));
}
