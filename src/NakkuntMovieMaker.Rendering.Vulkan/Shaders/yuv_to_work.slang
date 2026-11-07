// YUV の平面（NV12・P010）を、作業用の画像（premultiplied・16 ビット浮動小数点、不透明）にする。
// CPU の YuvConversion.ToBgra と同じ式（行列・範囲・色差の位置で線形に補間）。入力の色の変換（ColorLut）があれば、その後にかける
import work;
import colorlut;

struct Params
{
    int width;
    int height;
    int lumaStride;
    int chromaOffset;
    int chromaStride;
    int chromaWidth;
    int chromaHeight;
    int p010;

    // 行列の係数と、標本の値 → 0〜1 の Y と −0.5〜0.5 の Cb・Cr の式
    float kr;
    float kb;
    float yOffset;
    float yScale;
    float cOffset;
    float cScale;

    // 色差の標本 0 の位置（輝度の画素の番号）
    float originX;
    float originY;

    // 入力の色の変換（to_bgra8 と同じ並び）
    int useLut;
    int shaperLength;
    int cubeSize;
    int cubeOffset;
    int lutEncodeSrgb;
};

[[vk::push_constant]] ConstantBuffer<Params> p;
[[vk::binding(0, 0)]] StructuredBuffer<uint> planes;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint2> target;
[[vk::binding(2, 0)]] StructuredBuffer<float> lut;

// バッファーの中の、バイト単位の位置の標本（8 ビットなら 1 バイト、P010 なら 2 バイトの上位 10 ビット）
float sampleAt(int offset)
{
    if (p.p010 != 0)
    {
        uint word = planes[offset >> 2];
        uint value = (word >> uint((offset & 2) * 8)) & 0xFFFFu;
        return float(value >> 6);
    }

    uint word = planes[offset >> 2];
    return float((word >> uint((offset & 3) * 8)) & 255u);
}

int bytesPerSample()
{
    return p.p010 != 0 ? 2 : 1;
}

float chromaAt(int i, int j, int channel)
{
    i = clamp(i, 0, p.chromaWidth - 1);
    j = clamp(j, 0, p.chromaHeight - 1);
    return sampleAt(p.chromaOffset + j * p.chromaStride + (i * 2 + channel) * bytesPerSample());
}

float chroma(float u, float v, int channel)
{
    int u0 = int(floor(u));
    int v0 = int(floor(v));
    float fu = u - float(u0);
    float fv = v - float(v0);
    float top = lerp(chromaAt(u0, v0, channel), chromaAt(u0 + 1, v0, channel), fu);
    float bottom = lerp(chromaAt(u0, v0 + 1, channel), chromaAt(u0 + 1, v0 + 1, channel), fu);
    return lerp(top, bottom, fv);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    int x = int(id.x);
    int y = int(id.y);
    if (x >= p.width || y >= p.height)
    {
        return;
    }

    float luma = (sampleAt(y * p.lumaStride + x * bytesPerSample()) - p.yOffset) * p.yScale;
    float u = (float(x) - p.originX) / 2.0;
    float v = (float(y) - p.originY) / 2.0;
    float cb = (chroma(u, v, 0) - p.cOffset) * p.cScale;
    float cr = (chroma(u, v, 1) - p.cOffset) * p.cScale;

    float r = luma + 2.0 * (1.0 - p.kr) * cr;
    float b = luma + 2.0 * (1.0 - p.kb) * cb;
    float g = (luma - p.kr * r - p.kb * b) / (1.0 - p.kr - p.kb);
    float3 rgb = clamp(float3(r, g, b), 0.0, 1.0);
    if (p.useLut != 0)
    {
        rgb = nmmApplyColorLut(lut, p.shaperLength, p.cubeSize, p.cubeOffset, p.lutEncodeSrgb, rgb);
    }

    target[id.y * uint(p.width) + id.x] = nmmPackWork(float4(rgb, 1.0));
}
