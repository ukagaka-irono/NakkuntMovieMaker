using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Core.Model;
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

    /// <summary>ピクセル座標での範囲</summary>
    public PixelBounds PixelBounds => new(pixelLeft, pixelTop, width, height);

    public void Dispose() => buffer.Dispose();
}

/// <summary>ピクセル座標の範囲（左・上・幅・高さ）</summary>
public readonly record struct PixelBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public PixelBounds Inflate(int margin) => new(Left - margin, Top - margin, Width + margin * 2, Height + margin * 2);

    public PixelBounds Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    public PixelBounds Union(PixelBounds other)
    {
        var left = Math.Min(Left, other.Left);
        var top = Math.Min(Top, other.Top);
        return new PixelBounds(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }
}

/// <summary>2 つの作業用の画像の重ね方</summary>
public enum CombineMode
{
    /// <summary>source を上に重ねる</summary>
    SourceOver,

    /// <summary>source を下に重ねる</summary>
    DestinationOver,

    /// <summary>スクリーン（source に係数を掛ける）</summary>
    Screen,

    /// <summary>source を写す（source の外は透明）</summary>
    Copy,
}

/// <summary>エフェクトを GPU でかけるときに使うもの（GPU のスレッドで使う）</summary>
public sealed class GpuEffectContext(GpuCommandContext command, GpuAllocator allocator, ShaderLibrary shaders, IMediaSource media)
{
    public GpuCommandContext Command => command;

    public GpuAllocator Allocator => allocator;

    public ShaderLibrary Shaders => shaders;

    public IMediaSource Media => media;

    /// <summary>作業用の画像がリニアか（トランジションで、指定した色をリニアにするのに使う。エフェクトの中は常に sRGB）</summary>
    public bool Linear { get; init; }

    /// <summary>ピクセル座標の範囲の、透明な画像（中身は未定義なので、塗るか写してから使う）</summary>
    public GpuEffectImage CreateImage(int pixelLeft, int pixelTop, int width, int height, float scale)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var buffer = allocator.CreateBuffer((ulong)width * (ulong)height * 8, VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        return new GpuEffectImage(buffer, width, height, pixelLeft, pixelTop, scale);
    }

    /// <summary>ピクセル座標の範囲の、透明な画像</summary>
    public GpuEffectImage CreateClearImage(int pixelLeft, int pixelTop, int width, int height, float scale)
    {
        var image = CreateImage(pixelLeft, pixelTop, width, height, scale);
        Map("fx_clear", image, image.Count);
        return image;
    }

    /// <summary>
    /// ぼかした画像（ピクセル座標の範囲 bounds）。値は、入力を (shiftX, shiftY) だけずらしてから、標準偏差 sigma のぼかしをかけたもの。
    /// 入力の外は透明として扱う。ぼかし方は CPU 版（Skia）と同じく、3 回の箱型のぼかし（SVG の feGaussianBlur の方法）で、
    /// 箱の幅が 1 以下（sigma が 0.7 くらいまで）のときは、ずらすだけ
    /// </summary>
    public GpuEffectImage Blur(GpuEffectImage input, float sigma, float shiftX, float shiftY, PixelBounds bounds)
    {
        var kernel = BlurKernel(sigma);
        var radius = kernel.Length / 2;
        var weights = allocator.CreateBuffer((ulong)(kernel.Length * sizeof(float)), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Upload);
        MemoryMarshal.AsBytes(kernel.AsSpan()).CopyTo(weights.Span);
        weights.Flush();
        Release(weights);
        var pass = shaders.Get("fx_blur_pass", ThreeBuffers, Marshal.SizeOf<BlurPass>());

        // 横にぼかす（行は入力と同じ）→ 縦にぼかす
        var middle = CreateImage(bounds.Left, input.PixelTop, bounds.Width, input.Height, input.Scale);
        pass.Dispatch(command, [input.Buffer, middle.Buffer, weights], BlurPass.Between(input, middle, horizontal: true, radius, shiftX),
            ComputePipeline.Groups(middle.Width, 8), ComputePipeline.Groups(middle.Height, 8));
        GpuBarriers.AfterCompute(command);
        var output = CreateImage(bounds.Left, bounds.Top, bounds.Width, bounds.Height, input.Scale);
        pass.Dispatch(command, [middle.Buffer, output.Buffer, weights], BlurPass.Between(middle, output, horizontal: false, radius, shiftY),
            ComputePipeline.Groups(output.Width, 8), ComputePipeline.Groups(output.Height, 8));
        GpuBarriers.AfterCompute(command);
        Release(middle);
        return output;
    }

    /// <summary>
    /// 標準偏差 sigma のぼかしの重み（中心が真ん中、合計が 1）。Skia の CPU 版と同じく、幅 d = floor(σ·3·√(2π)/4 + 0.5) の箱型のぼかしを 3 回かけたもの。
    /// d が奇数なら 3 回とも中心に置き、偶数なら幅 d の 2 回を半画素ずつ左右にずらし、幅 d + 1 の 1 回を中心に置く
    /// </summary>
    public static float[] BlurKernel(float sigma)
    {
        var d = (int)Math.Floor(sigma * 3 * Math.Sqrt(2 * Math.PI) / 4 + 0.5);
        if (d <= 1)
        {
            return [1f];
        }

        // 箱は、中心からのずれの範囲 [from, to]
        (int From, int To)[] boxes = d % 2 == 1
            ? [(-(d - 1) / 2, (d - 1) / 2), (-(d - 1) / 2, (d - 1) / 2), (-(d - 1) / 2, (d - 1) / 2)]
            : [(-d / 2, d / 2 - 1), (-d / 2 + 1, d / 2), (-d / 2, d / 2)];
        var weights = new Dictionary<int, double> { [0] = 1 };
        foreach (var (from, to) in boxes)
        {
            var next = new Dictionary<int, double>();
            foreach (var (offset, weight) in weights)
            {
                for (var k = from; k <= to; k++)
                {
                    next[offset + k] = next.GetValueOrDefault(offset + k) + weight / (to - from + 1);
                }
            }

            weights = next;
        }

        var radius = weights.Keys.Max(Math.Abs);
        var kernel = new float[radius * 2 + 1];
        foreach (var (offset, weight) in weights)
        {
            kernel[offset + radius] = (float)weight;
        }

        return kernel;
    }

    /// <summary>target に source を、ピクセル座標を合わせて重ねる</summary>
    public void Combine(GpuEffectImage target, GpuEffectImage source, CombineMode mode, float factor = 1)
    {
        var push = new CombinePush(
            target.PixelLeft, target.PixelTop, target.Width, target.Height, source.PixelLeft, source.PixelTop, source.Width, source.Height, (int)mode, factor);
        shaders.Get("fx_combine", TwoBuffers, Marshal.SizeOf<CombinePush>())
            .Dispatch(command, [source.Buffer, target.Buffer], push, ComputePipeline.Groups(target.Width, 8), ComputePipeline.Groups(target.Height, 8));
        GpuBarriers.AfterCompute(command);
    }

    /// <summary>上下左右に margin ピクセル広げて、今の画像を写したもの</summary>
    public GpuEffectImage Expand(GpuEffectImage input, int margin)
    {
        var output = CreateImage(input.PixelLeft - margin, input.PixelTop - margin, input.Width + margin * 2, input.Height + margin * 2, input.Scale);
        Combine(output, input, CombineMode.Copy);
        return output;
    }

    /// <summary>色（straight、0〜1）で、画素の不透明度の形に塗る（Skia の SrcIn）</summary>
    public void Tint(GpuEffectImage image, Vector4 color) =>
        Map("fx_tint", image, new TintPush(image.Count, color.X, color.Y, color.Z, color.W));

    /// <summary>
    /// target に source を、変形して重ねる（合成のシェーダー。線形の補間と縁のアンチエイリアス）。
    /// inverse は、target の画素の番号（中心は +0.5）→ source のピクセル座標
    /// </summary>
    public void Composite(GpuEffectImage target, GpuEffectImage source, Matrix3x2 inverse)
    {
        var parameters = new VulkanBackend.CompositeParams
        {
            Ax = inverse.M11,
            Ay = inverse.M12,
            Bx = inverse.M21,
            By = inverse.M22,
            Cx = inverse.M31,
            Cy = inverse.M32,
            Left = source.PixelLeft,
            Top = source.PixelTop,
            Width = source.Width,
            Height = source.Height,
            SourceWidth = source.Width,
            SourceHeight = source.Height,
            Stride = source.Width,
            RegionWidth = target.Width,
            RegionHeight = target.Height,
            Opacity = 1,
            Antialias = 1,
            TargetWidth = target.Width,
            SourceFormat = 1,
        };
        shaders.Get("composite", VulkanBackend.CompositeBindings, Marshal.SizeOf<VulkanBackend.CompositeParams>())
            .Dispatch(command, [target.Buffer, source.Buffer], parameters, ComputePipeline.Groups(target.Width, 8), ComputePipeline.Groups(target.Height, 8));
        GpuBarriers.AfterCompute(command);
    }

    private static readonly GpuBindingKind[] TwoBuffers = [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer];
    private static readonly GpuBindingKind[] ThreeBuffers = [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer];

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct BlurPass(
        int SourceLeft, int SourceTop, int SourceWidth, int SourceHeight, int TargetLeft, int TargetTop, int TargetWidth, int TargetHeight,
        int Horizontal, int Radius, float Shift)
    {
        public static BlurPass Between(GpuEffectImage source, GpuEffectImage target, bool horizontal, int radius, float shift) => new(
            source.PixelLeft, source.PixelTop, source.Width, source.Height, target.PixelLeft, target.PixelTop, target.Width, target.Height,
            horizontal ? 1 : 0, radius, shift);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CombinePush(
        int TargetLeft, int TargetTop, int TargetWidth, int TargetHeight, int SourceLeft, int SourceTop, int SourceWidth, int SourceHeight, int Mode, float Factor);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TintPush(int Count, float R, float G, float B, float A);

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
        new MosaicGpu(), new SharpenGpu(), new BorderGpu(), new BlurGpu(), new DropShadowGpu(), new GlowGpu(), new MaskGpu(), new ShakeGpu(),
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

// 色（RgbaColor）を、0〜1 の straight の色にする（不透明度には opacity を掛けて、8 ビットに丸める。CPU 版の Blurs.ToSkia と同じ）
internal static class GpuColors
{
    public static Vector4 Straight(RgbaColor color, float opacity = 1) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, (float)Math.Round(color.A * Math.Clamp(opacity, 0, 1)) / 255f);
}

// モザイク（CPU 版の MosaicEffect と同じブロックの分け方。ブロックの範囲は CPU で表にする）
internal sealed class MosaicGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Mosaic.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var size = p.GetFloat("size") * input.Scale;
        if (size <= 1)
        {
            return input;
        }

        var bounds = input.PixelBounds;
        var (columnStarts, columnEnds, indexX) = Blocks(bounds.Left, bounds.Right, size);
        var (rowStarts, rowEnds, indexY) = Blocks(bounds.Top, bounds.Bottom, size);
        int[] table = [.. columnStarts, .. columnEnds, .. rowStarts, .. rowEnds, .. indexX, .. indexY];
        var columns = columnStarts.Length;
        var rows = rowStarts.Length;

        var buffer = context.Allocator.CreateBuffer((ulong)(table.Length * sizeof(int)), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Upload);
        MemoryMarshal.AsBytes(table.AsSpan()).CopyTo(buffer.Span);
        buffer.Flush();
        context.Release(buffer);
        var averages = context.Allocator.CreateBuffer((ulong)(columns * rows * 16), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        context.Release(averages);

        GpuBindingKind[] bindings = [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer];
        context.Shaders.Get("fx_mosaic_average", bindings, Marshal.SizeOf<AveragePush>())
            .Dispatch(context.Command, [input.Buffer, buffer, averages], new AveragePush(input.Width, columns, rows),
                ComputePipeline.Groups(columns, 8), ComputePipeline.Groups(rows, 8));
        GpuBarriers.AfterCompute(context.Command);
        var fill = new FillPush(input.Width, input.Height, columns, columns * 2 + rows * 2, columns * 2 + rows * 2 + input.Width);
        context.Shaders.Get("fx_mosaic_fill", bindings, Marshal.SizeOf<FillPush>())
            .Dispatch(context.Command, [input.Buffer, buffer, averages], fill, ComputePipeline.Groups(input.Width, 8), ComputePipeline.Groups(input.Height, 8));
        GpuBarriers.AfterCompute(context.Command);
        return input;
    }

    // 1 方向のブロックの範囲（画像の中の画素の番号）と、画素ごとのブロックの番号（どのブロックにも入らなければ -1）
    private static (int[] Starts, int[] Ends, int[] Index) Blocks(int start, int end, float size)
    {
        var first = (int)Math.Floor(start / size);
        var last = (int)Math.Floor((end - 1) / size);
        var count = last - first + 1;
        var starts = new int[count];
        var ends = new int[count];
        var index = new int[end - start];
        Array.Fill(index, -1);
        for (var i = 0; i < count; i++)
        {
            var block = first + i;
            starts[i] = Math.Max(start, (int)Math.Round(block * size)) - start;
            ends[i] = Math.Min(end, (int)Math.Round((block + 1) * size)) - start;
            for (var x = starts[i]; x < ends[i]; x++)
            {
                index[x] = i;
            }
        }

        return (starts, ends, index);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AveragePush(int Width, int Columns, int Rows);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FillPush(int Width, int Height, int Columns, int IndexX, int IndexY);
}

internal sealed class SharpenGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Sharpen.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var amount = p.GetFloat("amount") / 100f;
        var sigma = RenderMath.BlurSigma(p.GetNumber("radius"), input.Scale);
        if (amount <= 0 || sigma < 0.05f)
        {
            return input;
        }

        // 入力の範囲だけぼかせばよい（外側に広がる部分は使わない）
        var blurred = context.Blur(input, sigma, 0, 0, input.PixelBounds);
        context.Release(blurred);
        var push = new Push(input.Width, input.Height, 0, 0, blurred.Width, amount);
        context.Shaders.Get("fx_sharpen", [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer], Marshal.SizeOf<Push>())
            .Dispatch(context.Command, [input.Buffer, blurred.Buffer], push, ComputePipeline.Groups(input.Width, 8), ComputePipeline.Groups(input.Height, 8));
        GpuBarriers.AfterCompute(context.Command);
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(int Width, int Height, int OffsetX, int OffsetY, int BlurredWidth, float Amount);
}

// 縁取り（CPU 版の BorderEffect と同じ。距離変換も同じ方法で、列ごと・行ごとに GPU で行う）
internal sealed class BorderGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Border.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var width = p.GetFloat("width") * input.Scale;
        var color = p.GetColor("color");
        if (width <= 0 || color.A == 0)
        {
            return input;
        }

        var sigma = RenderMath.BlurSigma(p.GetNumber("blur"), input.Scale);
        var margin = (int)Math.Ceiling(width) + 1 + RenderMath.BlurMargin(sigma);
        var expanded = context.Expand(input, margin);
        context.Release(input);
        context.Release(expanded);

        // 不透明な部分からの距離の 2 乗
        var w = expanded.Width;
        var h = expanded.Height;
        var cells = (ulong)w * (ulong)h;
        var columns = context.Allocator.CreateBuffer(cells * sizeof(float), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        var distance = context.Allocator.CreateBuffer(cells * sizeof(float), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        var v = context.Allocator.CreateBuffer(cells * sizeof(int), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        var z = context.Allocator.CreateBuffer((ulong)(w + 1) * (ulong)(h + 1) * sizeof(float), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        foreach (var buffer in new[] { columns, distance, v, z })
        {
            context.Release(buffer);
        }

        GpuBindingKind[] four = [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer];
        var size = new SizePush(w, h);
        context.Shaders.Get("fx_distance_columns", four, Marshal.SizeOf<SizePush>())
            .Dispatch(context.Command, [expanded.Buffer, columns, v, z], size, ComputePipeline.Groups(w, 64), 1);
        GpuBarriers.AfterCompute(context.Command);
        context.Shaders.Get("fx_distance_rows", four, Marshal.SizeOf<SizePush>())
            .Dispatch(context.Command, [columns, distance, v, z], size, ComputePipeline.Groups(h, 64), 1);
        GpuBarriers.AfterCompute(context.Command);

        // 縁の形を塗り、ぼかし、元の画像を上に重ねる
        var border = context.CreateImage(expanded.PixelLeft, expanded.PixelTop, w, h, expanded.Scale);
        var straight = GpuColors.Straight(color);
        var fill = new FillPush(border.Count, width, straight.X, straight.Y, straight.Z, straight.W);
        context.Shaders.Get("fx_border_fill", [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer], Marshal.SizeOf<FillPush>())
            .Dispatch(context.Command, [distance, border.Buffer], fill, ComputePipeline.Groups(border.Count, 256), 1);
        GpuBarriers.AfterCompute(context.Command);
        if (sigma >= 0.05f)
        {
            var blurred = context.Blur(border, sigma, 0, 0, border.PixelBounds);
            context.Release(border);
            border = blurred;
        }

        context.Combine(border, expanded, CombineMode.SourceOver);
        return border;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SizePush(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FillPush(int Count, float Width, float R, float G, float B, float A);
}

internal sealed class BlurGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Blur.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var sigma = RenderMath.BlurSigma(p.GetNumber("radius"), input.Scale);
        if (sigma < 0.05f)
        {
            return input;
        }

        var output = context.Blur(input, sigma, 0, 0, input.PixelBounds.Inflate(RenderMath.BlurMargin(sigma)));
        context.Release(input);
        return output;
    }
}

// ドロップシャドウ（CPU 版の DropShadowEffect と同じ範囲）：ずらしてぼかした形を影の色で塗り、元の画像を上に重ねる
internal sealed class DropShadowGpu : IGpuEffect
{
    public string Type => BuiltInEffects.DropShadow.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var dx = p.GetFloat("offsetX") * input.Scale;
        var dy = p.GetFloat("offsetY") * input.Scale;
        var sigma = RenderMath.BlurSigma(p.GetNumber("blur"), input.Scale);
        var color = GpuColors.Straight(p.GetColor("color"), p.GetFloat("opacity") / 100f);
        if (color.W <= 0)
        {
            return input;
        }

        var bounds = input.PixelBounds;
        var shadow = bounds.Offset((int)Math.Floor(dx), (int)Math.Floor(dy)).Inflate(RenderMath.BlurMargin(sigma) + 1);
        var output = context.Blur(input, sigma, dx, dy, bounds.Union(shadow));
        context.Tint(output, color);
        context.Combine(output, input, CombineMode.SourceOver);
        context.Release(input);
        return output;
    }
}

// 発光（CPU 版の GlowEffect と同じ）：ぼかした画像（色を指定したときはその色に塗ったもの）を、スクリーンで重ねる
internal sealed class GlowGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Glow.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var strength = p.GetFloat("strength") / 100f;
        var sigma = RenderMath.BlurSigma(p.GetNumber("radius"), input.Scale);
        if (strength <= 0 || sigma < 0.05f)
        {
            return input;
        }

        var bounds = input.PixelBounds.Inflate(RenderMath.BlurMargin(sigma));
        var glow = context.Blur(input, sigma, 0, 0, bounds);
        context.Release(glow);
        if (p.GetBool("tint"))
        {
            context.Tint(glow, GpuColors.Straight(p.GetColor("color")));
        }

        var output = context.CreateImage(bounds.Left, bounds.Top, bounds.Width, bounds.Height, input.Scale);
        context.Combine(output, input, CombineMode.Copy);
        context.Release(input);
        for (var remaining = strength; remaining > 0; remaining -= 1)
        {
            context.Combine(output, glow, CombineMode.Screen, (float)Math.Round(Math.Min(remaining, 1) * 255) / 255f);
        }

        return output;
    }
}

// マスク（CPU 版の MaskEffect と同じ図形）：図形までの距離から、残す割合を求めて掛ける
internal sealed class MaskGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Mask.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var width = p.GetFloat("width");
        var height = p.GetFloat("height");
        var angle = p.GetFloat("rotation") * MathF.PI / 180f;
        var sigma = RenderMath.BlurSigma(p.GetNumber("feather"), 1);
        var push = new Push(
            input.Width, input.Height, input.PixelLeft, input.PixelTop, input.Scale, p.GetFloat("x"), p.GetFloat("y"), MathF.Cos(angle), MathF.Sin(angle),
            width / 2, height / 2, Math.Max(0, Math.Min(p.GetFloat("cornerRadius"), Math.Min(width, height) / 2)),
            p.GetText("shape") == "ellipse" ? 1 : 0, sigma >= 0.05f ? sigma : 0, p.GetBool("invert") ? 1 : 0);
        context.Shaders.Get("fx_mask", [GpuBindingKind.StorageBuffer], Marshal.SizeOf<Push>())
            .Dispatch(context.Command, [input.Buffer], push, ComputePipeline.Groups(input.Width, 8), ComputePipeline.Groups(input.Height, 8));
        GpuBarriers.AfterCompute(context.Command);
        return input;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Push(
        int Width, int Height, int Left, int Top, float Scale, float X, float Y, float Cosine, float Sine,
        float HalfWidth, float HalfHeight, float Radius, int Ellipse, float Sigma, int Invert);
}

// 振動（CPU 版の ShakeEffect と同じ）：ピクセル座標で、原点のまわりに回してからずらす。描くのは合成のシェーダー（線形の補間と縁のアンチエイリアス）
internal sealed class ShakeGpu : IGpuEffect
{
    public string Type => BuiltInEffects.Shake.Type;

    public GpuEffectImage Apply(GpuEffectContext context, GpuEffectImage input, EffectParameters p)
    {
        var dx = p.GetFloat("offsetX") * input.Scale;
        var dy = p.GetFloat("offsetY") * input.Scale;
        var angle = p.GetFloat("angle");
        if (dx == 0 && dy == 0 && angle == 0)
        {
            return input;
        }

        var matrix = Matrix3x2.CreateRotation(angle * MathF.PI / 180f) * Matrix3x2.CreateTranslation(dx, dy);
        var corners = new[]
        {
            Vector2.Transform(new Vector2(input.PixelLeft, input.PixelTop), matrix),
            Vector2.Transform(new Vector2(input.PixelLeft + input.Width, input.PixelTop), matrix),
            Vector2.Transform(new Vector2(input.PixelLeft, input.PixelTop + input.Height), matrix),
            Vector2.Transform(new Vector2(input.PixelLeft + input.Width, input.PixelTop + input.Height), matrix),
        };
        var left = (int)MathF.Floor(corners.Min(c => c.X));
        var top = (int)MathF.Floor(corners.Min(c => c.Y));
        var right = (int)MathF.Ceiling(corners.Max(c => c.X));
        var bottom = (int)MathF.Ceiling(corners.Max(c => c.Y));
        var output = context.CreateClearImage(left, top, right - left, bottom - top, input.Scale);

        // 出力の画素の番号 → 入力のピクセル座標
        if (!Matrix3x2.Invert(matrix * Matrix3x2.CreateTranslation(-left, -top), out var inverse))
        {
            context.Release(output);
            return input;
        }

        context.Composite(output, input, inverse);
        context.Release(input);
        return output;
    }
}
