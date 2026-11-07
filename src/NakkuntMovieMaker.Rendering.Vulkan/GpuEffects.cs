using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Rendering.Color;
using NakkuntMovieMaker.Rendering.Effects;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// エフェクトをかける途中の画像（GPU のバッファー。premultiplied の RGBA を 16 ビット浮動小数点で持つ）。
/// CPU 版の EffectImage と同じく、ビットマップの左上はピクセル座標（ローカル座標を Scale 倍したもの）の (PixelLeft, PixelTop)
/// </summary>
public sealed class GpuEffectImage(GpuBuffer buffer, int width, int height, int pixelLeft, int pixelTop, float scale) : IDisposable
{
    public GpuBuffer Buffer => buffer;

    public int Width => width;

    public int Height => height;

    public int PixelLeft => pixelLeft;

    public int PixelTop => pixelTop;

    public float Scale => scale;

    public int Count => width * height;

    /// <summary>ローカル座標での範囲（左・上・幅・高さ）</summary>
    public (float Left, float Top, float Width, float Height) LocalBounds => (pixelLeft / scale, pixelTop / scale, width / scale, height / scale);

    public long Bytes => buffer.Bytes;

    public void Dispose() => buffer.Dispose();
}

/// <summary>エフェクトを GPU でかけるときに使うもの（GPU のスレッドで使う）</summary>
public sealed class GpuEffectContext(GpuCommandContext command, GpuAllocator allocator, ShaderLibrary shaders, IMediaSource media)
{
    public GpuCommandContext Command => command;

    public GpuAllocator Allocator => allocator;

    public ShaderLibrary Shaders => shaders;

    public IMediaSource Media => media;

    /// <summary>ピクセル座標の範囲の、透明な画像（中身は未定義なので、塗るか写してから使う）</summary>
    public GpuEffectImage CreateImage(int pixelLeft, int pixelTop, int width, int height, float scale)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var buffer = allocator.CreateBuffer((ulong)width * (ulong)height * 8, VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        return new GpuEffectImage(buffer, width, height, pixelLeft, pixelTop, scale);
    }

    /// <summary>この仕事が GPU で終わったら捨てる</summary>
    public void Release(IDisposable resource) => command.OnCompleted(resource.Dispose);

    /// <summary>1 画素ずつ処理するシェーダー（256 個ずつのワークグループ）を実行し、書き込みを後の処理に見せる</summary>
    public void Map<TPush>(string shader, GpuEffectImage image, in TPush push)
        where TPush : unmanaged
    {
        shaders.Get(shader, [GpuBindingKind.StorageBuffer], Marshal.SizeOf<TPush>())
            .Dispatch(command, [image.Buffer], push, ComputePipeline.Groups(image.Count, 256), 1);
        GpuBarriers.AfterCompute(command);
    }
}

/// <summary>エフェクトの GPU での処理。input を書き換えてよく、そのまま返してもよい。新しい画像を返したときは、input を Release で捨てること</summary>
public interface IGpuEffect
{
    string Type { get; }

    GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters parameters);
}

/// <summary>組み込みのエフェクトの GPU での処理の一覧（まだないものは、CPU 版で描く）</summary>
public static class GpuEffects
{
    private static readonly FrozenDictionary<string, IGpuEffect> ByType = new IGpuEffect[]
    {
        new ColorAdjustGpu(), new MonochromeGpu(), new ChromaKeyGpu(), new ColorKeyGpu(), new NoiseGpu(), new LutGpu(), new FadeGpu(),
    }.ToFrozenDictionary(effect => effect.Type, StringComparer.Ordinal);

    public static IGpuEffect? Find(string type) => ByType.GetValueOrDefault(type);

    /// <summary>すべてのエフェクトを GPU でかけられるか</summary>
    public static bool Supports(IEnumerable<EvaluatedEffect> effects) => effects.All(effect => ByType.ContainsKey(effect.Type));
}

// 色調補正（CPU 版の ColorAdjustEffect と同じ値の決め方）
internal sealed class ColorAdjustGpu : IGpuEffect
{
    public string Type => BuiltInEffects.ColorAdjust.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var contrast = p.GetFloat("contrast") / 100f;
        var hue = p.GetFloat("hue") * MathF.PI / 180f;
        var push = new Push(
            input.Count,
            1f / Math.Max(p.GetFloat("gamma"), 0.01f),
            p.GetFloat("brightness") / 100f,
            contrast >= 0 ? 1f / (1f - contrast * 0.99f) : 1f + contrast,
            p.GetFloat("temperature") / 100f,
            1f + p.GetFloat("saturation") / 100f,
            MathF.Cos(hue),
            MathF.Sin(hue));
        if (push is { InverseGamma: 1, Brightness: 0, Contrast: 1, Temperature: 0, Saturation: 1, HueCos: 1, HueSin: 0 })
        {
            return input;
        }

        context.Map("fx_color_adjust", input, push);
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Count, float InverseGamma, float Brightness, float Contrast, float Temperature, float Saturation, float HueCos, float HueSin);
}

internal sealed class MonochromeGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Monochrome.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var strength = p.GetFloat("strength") / 100f;
        if (strength <= 0)
        {
            return input;
        }

        var tint = p.GetColor("color");
        context.Map("fx_monochrome", input, new Push(input.Count, strength, tint.R / 255f, tint.G / 255f, tint.B / 255f));
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Count, float Strength, float TintR, float TintG, float TintB);
}

internal sealed class ChromaKeyGpu : IGpuEffect
{
    public string Type => BuiltInEffects.ChromaKey.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var key = p.GetColor("color");
        var rgb = new Vector3(key.R, key.G, key.B) / 255f;
        var luma = rgb.X * 0.2126f + rgb.Y * 0.7152f + rgb.Z * 0.0722f;
        var chroma = new Vector2((rgb.Z - luma) / 1.8556f, (rgb.X - luma) / 1.5748f);
        var length = chroma.Length();
        var direction = length > 0 ? chroma / length : Vector2.Zero;
        context.Map("fx_chroma_key", input, new Push(
            input.Count, chroma.X, chroma.Y, direction.X, direction.Y,
            p.GetFloat("tolerance") / 100f * 0.5f, p.GetFloat("softness") / 100f * 0.5f, p.GetFloat("spill") / 100f));
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Count, float KeyX, float KeyY, float DirectionX, float DirectionY, float Tolerance, float Softness, float Spill);
}

internal sealed class ColorKeyGpu : IGpuEffect
{
    public string Type => BuiltInEffects.ColorKey.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var key = p.GetColor("color");
        context.Map("fx_color_key", input, new Push(input.Count, key.R / 255f, key.G / 255f, key.B / 255f, p.GetFloat("tolerance") / 100f, p.GetFloat("softness") / 100f));
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Count, float KeyR, float KeyG, float KeyB, float Tolerance, float Softness);
}

internal sealed class NoiseGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Noise.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var amount = p.GetFloat("amount") / 100f;
        if (amount <= 0)
        {
            return input;
        }

        var seed = unchecked((uint)(long)p.GetNumber("seed") * 0x01000193u ^ (uint)(long)p.GetNumber("frame"));
        var push = new Push(input.Width, input.Height, input.PixelLeft, input.PixelTop, seed, amount, p.GetBool("monochrome") ? 1 : 0);
        context.Shaders.Get("fx_noise", [GpuBindingKind.StorageBuffer], Marshal.SizeOf<Push>())
            .Dispatch(context.Command, [input.Buffer], push, ComputePipeline.Groups(input.Width, 8), ComputePipeline.Groups(input.Height, 8));
        GpuBarriers.AfterCompute(context.Command);
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Width, int Height, int Left, int Top, uint Seed, float Amount, int Monochrome);
}

internal sealed class LutGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Lut.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var file = p.GetText("file");
        var intensity = p.GetFloat("intensity") / 100f;
        if (string.IsNullOrWhiteSpace(file) || intensity <= 0 || CubeLut.TryLoad(context.Media.ResolvePath(file)) is not { } lut)
        {
            return input;
        }

        // LUT の格子は、この仕事の間だけ GPU に置く（小さいので、覚えておくのは後で）
        var table = context.Allocator.CreateBuffer((ulong)(lut.Table.Length * 3 * sizeof(float)), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Upload);
        MemoryMarshal.Cast<Vector3, byte>(lut.Table).CopyTo(table.Span);
        table.Flush();
        context.Release(table);
        var push = new Push(input.Count, lut.Size, intensity, lut.DomainMin.X, lut.DomainMin.Y, lut.DomainMin.Z, lut.DomainScale.X, lut.DomainScale.Y, lut.DomainScale.Z);
        context.Shaders.Get("fx_lut", [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer], Marshal.SizeOf<Push>())
            .Dispatch(context.Command, [input.Buffer, table], push, ComputePipeline.Groups(input.Count, 256), 1);
        GpuBarriers.AfterCompute(context.Command);
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Count, int Size, float Intensity, float MinR, float MinG, float MinB, float ScaleR, float ScaleG, float ScaleB);
}

internal sealed class FadeGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Fade.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var alpha = p.GetFloat("alpha");
        if (alpha < 1)
        {
            context.Map("fx_fade", input, new Push(input.Count, Math.Clamp(alpha, 0, 1)));
        }

        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Count, float Factor);
}
