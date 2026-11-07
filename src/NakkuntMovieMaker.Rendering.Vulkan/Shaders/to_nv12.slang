// 作業用の画像を、書き出し用の NV12（8 ビット、4:2:0）にする。色は to_bgra8 と同じ（リニアなら sRGB にし、出力の変換をかける）。
// premultiplied の色をそのまま使う（黒の上に重ねたのと同じ。swscale に BGRA を渡したときと同じ）。
// 1 つのスレッドが、横 4 × 縦 2 の画素（輝度 8 個・色差 2 組）を受け持つ。色差の位置は左（MPEG-2・H.264 の既定）：
// 横は 1:2:1 の重みで、縦は 2 行の平均で求める
import work;
import colorlut;

struct Params
{
    int width;
    int height;
    int lumaStride;
    int chromaOffset;
    int chromaStride;
    int encodeSrgb;

    // 出力の変換（to_bgra8 と同じ）
    int useLut;
    int shaperLength;
    int cubeSize;
    int cubeOffset;
    int lutEncodeSrgb;

    // 行列の係数と範囲（Y' = yOffset + yScale·Y、C' = 128 + cScale·C。リミテッドなら 16・219・224、フルなら 0・255・255）
    float kr;
    float kb;
    float yOffset;
    float yScale;
    float cScale;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> source;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> output;
[[vk::binding(2, 0)]] StructuredBuffer<float> lut;

// 書き出す色（R'G'B'、0〜1）
float3 colorAt(int x, int y)
{
    x = clamp(x, 0, p.width - 1);
    y = clamp(y, 0, p.height - 1);
    float4 c = nmmUnpackWork(source[y * p.width + x]);
    if (p.encodeSrgb != 0)
    {
        c = clamp(c, 0.0, 1.0);
        c = nmmPremultipliedToSrgb(float4(min(c.rgb, c.a), c.a));
    }

    if (p.useLut != 0 && c.a > 0.0)
    {
        c = clamp(c, 0.0, 1.0);
        c = float4(nmmApplyColorLut(lut, p.shaperLength, p.cubeSize, p.cubeOffset, p.lutEncodeSrgb, min(c.rgb, c.a) / c.a) * c.a, c.a);
    }

    return clamp(c.rgb, 0.0, 1.0);
}

float luma(float3 rgb)
{
    return p.kr * rgb.r + (1.0 - p.kr - p.kb) * rgb.g + p.kb * rgb.b;
}

uint unorm(float value)
{
    return uint(clamp(floor(value + 0.5), 0.0, 255.0));
}

// 色差の標本 i・行の組 j の (Cb, Cr)（−0.5〜0.5）
float2 chromaAt(int i, int j)
{
    float2 sum = float2(0.0);
    for (int row = 0; row < 2; row++)
    {
        for (int tap = -1; tap <= 1; tap++)
        {
            float weight = tap == 0 ? 0.5 : 0.25;
            float3 rgb = colorAt(2 * i + tap, 2 * j + row);
            float y = luma(rgb);
            sum += weight * float2((rgb.b - y) / (2.0 * (1.0 - p.kb)), (rgb.r - y) / (2.0 * (1.0 - p.kr)));
        }
    }

    return sum * 0.5;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    int gx = int(id.x);
    int gy = int(id.y);
    if (gx * 4 >= p.width || gy * 2 >= p.height)
    {
        return;
    }

    // 輝度：2 行 × 4 画素（幅の外は 0）
    for (int row = 0; row < 2; row++)
    {
        int y = gy * 2 + row;
        if (y >= p.height)
        {
            break;
        }

        uint packed = 0u;
        for (int column = 0; column < 4; column++)
        {
            int x = gx * 4 + column;
            if (x < p.width)
            {
                packed |= unorm(p.yOffset + p.yScale * luma(colorAt(x, y))) << uint(column * 8);
            }
        }

        output[(y * p.lumaStride + gx * 4) / 4] = packed;
    }

    // 色差：2 組（Cb, Cr, Cb, Cr）
    uint chroma = 0u;
    int chromaWidth = (p.width + 1) / 2;
    for (int sample = 0; sample < 2; sample++)
    {
        int i = gx * 2 + sample;
        if (i < chromaWidth)
        {
            float2 c = chromaAt(i, gy);
            chroma |= unorm(128.0 + p.cScale * c.x) << uint(sample * 16);
            chroma |= unorm(128.0 + p.cScale * c.y) << uint(sample * 16 + 8);
        }
    }

    output[(p.chromaOffset + gy * p.chromaStride + gx * 4) / 4] = chroma;
}
