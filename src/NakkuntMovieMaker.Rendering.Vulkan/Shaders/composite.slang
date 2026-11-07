// 素材の画像を、変形・切り抜き・不透明度・合成モードを付けて、作業用の画像（RGBA16F・premultiplied）に重ねる。
// CPU 版（SkiaCpuBackend の DrawImage：線形の補間・縁のアンチエイリアス・Skia の合成の式）と同じ結果になるように計算する
import common;

struct Params
{
    // 出力の画素の中心（デバイスの座標）→ ローカル座標：local = (ax·x + bx·y + cx, ay·x + by·y + cy)
    float ax;
    float ay;
    float bx;
    float by;
    float cx;
    float cy;

    // 素材を描く四角形（ローカル座標）
    float left;
    float top;
    float width;
    float height;

    // 素材の画像の中の、使う部分（画素）と、画像の幅（1 行の画素の数）
    int sourceX;
    int sourceY;
    int sourceWidth;
    int sourceHeight;
    int stride;

    // 計算する出力の範囲（画素）
    int originX;
    int originY;
    int regionWidth;
    int regionHeight;

    float opacity;
    int blend;
    int antialias;
    int padding;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] [format("rgba16f")] RWTexture2D<float4> target;
[[vk::binding(1, 0)]] StructuredBuffer<uint> source;

// premultiplied の BGRA（8 ビット）を 1 画素読む（範囲の外は端の画素）
float4 texel(int x, int y)
{
    x = clamp(x, 0, p.sourceWidth - 1);
    y = clamp(y, 0, p.sourceHeight - 1);
    uint bgra = source[(p.sourceY + y) * p.stride + p.sourceX + x];
    return float4(float((bgra >> 16) & 255u), float((bgra >> 8) & 255u), float(bgra & 255u), float(bgra >> 24)) / 255.0;
}

// 線形の補間（画素の中心は +0.5）
float4 sampleBilinear(float u, float v)
{
    float x = u - 0.5;
    float y = v - 0.5;
    int x0 = int(floor(x));
    int y0 = int(floor(y));
    float fx = x - float(x0);
    float fy = y - float(y0);
    float4 top = lerp(texel(x0, y0), texel(x0 + 1, y0), fx);
    float4 bottom = lerp(texel(x0, y0 + 1), texel(x0 + 1, y0 + 1), fx);
    return lerp(top, bottom, fy);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (int(id.x) >= p.regionWidth || int(id.y) >= p.regionHeight)
    {
        return;
    }

    int2 pixel = int2(p.originX + int(id.x), p.originY + int(id.y));
    float2 device = float2(pixel) + 0.5;
    float lx = p.ax * device.x + p.bx * device.y + p.cx;
    float ly = p.ay * device.x + p.by * device.y + p.cy;

    // 四角形の縁までの距離（出力の画素の単位）から、覆っている割合を求める
    float coverage = 1.0;
    float gx = length(float2(p.ax, p.bx));
    float gy = length(float2(p.ay, p.by));
    float dx = min(lx - p.left, p.left + p.width - lx) / max(gx, 1e-12);
    float dy = min(ly - p.top, p.top + p.height - ly) / max(gy, 1e-12);
    if (p.antialias != 0)
    {
        coverage = clamp(dx + 0.5, 0.0, 1.0) * clamp(dy + 0.5, 0.0, 1.0);
    }
    else if (dx < 0.0 || dy < 0.0)
    {
        coverage = 0.0;
    }

    if (coverage <= 0.0)
    {
        return;
    }

    float u = (lx - p.left) / p.width * float(p.sourceWidth);
    float v = (ly - p.top) / p.height * float(p.sourceHeight);
    float4 s = sampleBilinear(u, v) * p.opacity;
    float4 d = target[pixel];
    float4 blended = nmmBlend(p.blend, s, d);
    target[pixel] = lerp(d, blended, coverage);
}
