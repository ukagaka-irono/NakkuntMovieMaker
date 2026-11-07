// トランジションの混ぜ方（CPU 版の CpuTransitions.Mix・DipTransition と同じ）。結果を target の上に重ねる
// mode：0 は From·fromAlpha + To·toAlpha、1 は軸に沿ったグラデーション（start で To が 1、end で From が 1。画素の中心で求める）、
// 2 は From に色を SrcATop で重ねたもの
import work;

struct Params
{
    int width;
    int height;
    int mode;
    float fromAlpha;
    float toAlpha;
    int horizontal;
    float start;
    float end;
    float r;
    float g;
    float b;
    float a;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> target;
[[vk::binding(1, 0)]] StructuredBuffer<uint2> from;
[[vk::binding(2, 0)]] StructuredBuffer<uint2> to;

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.width || int(id.y) >= p.height)
    {
        return;
    }

    uint index = id.y * uint(p.width) + id.x;
    float4 f = nmmUnpackWork(from[index]);
    float4 s;
    if (p.mode == 2)
    {
        s = float4(p.r * p.a, p.g * p.a, p.b * p.a, p.a) * f.a + f * (1.0 - p.a);
    }
    else
    {
        float fromAlpha = p.fromAlpha;
        float toAlpha = p.toAlpha;
        if (p.mode == 1)
        {
            float coordinate = float(p.horizontal != 0 ? id.x : id.y) + 0.5;
            float t = clamp((coordinate - p.start) / (p.end - p.start), 0.0, 1.0);
            fromAlpha = t;
            toAlpha = 1.0 - t;
        }

        s = f * fromAlpha + nmmUnpackWork(to[index]) * toAlpha;
    }

    float4 d = nmmUnpackWork(target[index]);
    target[index] = nmmPackWork(s + d * (1.0 - s.a));
}
