using System.Numerics;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Rendering.Color;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>シェーダーの共通の計算が、CPU 版（C#）と同じ結果になるか</summary>
public sealed class ShaderTests
{
    [StructLayout(LayoutKind.Sequential)]
    private record struct NoiseParams(uint Seed, int OriginX, int OriginY, int Width, int Height, int Channel);

    [StructLayout(LayoutKind.Sequential)]
    private record struct LutParams(Vector3 DomainMin, int Size, Vector3 DomainScale, int Count);

    [Fact]
    public async Task NoiseHashMatchesTheCpuBitForBit()
    {
        VulkanDevice.ClearValidationMessages();
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        using var library = new ShaderLibrary(device);
        using var scheduler = new GpuScheduler(device);
        const int Width = 70;
        const int Height = 50;
        var parameters = new NoiseParams(123456789, -35, -20, Width, Height, 2); // 負の座標も含める
        using var output = allocator.CreateBuffer(Width * Height * sizeof(float), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Readback);
        var pipeline = library.Get(typeof(ShaderTests).Assembly, "test_noise", [GpuBindingKind.StorageBuffer], Marshal.SizeOf<NoiseParams>());

        var fence = await scheduler.RunAsync(context =>
        {
            pipeline.Dispatch(context, [output], parameters, ComputePipeline.Groups(Width, 8), ComputePipeline.Groups(Height, 8));
            GpuBarriers.AfterCompute(context);
            return context.Fence;
        }, TestContext.Current.CancellationToken);
        fence.Wait(TestContext.Current.CancellationToken);
        output.Invalidate();

        var gpu = MemoryMarshal.Cast<byte, float>(output.Span).ToArray();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cpu = RenderMath.Hash(parameters.Seed, parameters.OriginX + x, parameters.OriginY + y, parameters.Channel);
                Assert.True(
                    BitConverter.SingleToInt32Bits(cpu) == BitConverter.SingleToInt32Bits(gpu[y * Width + x]),
                    $"({x}, {y}): CPU {cpu} と GPU {gpu[y * Width + x]} が違う");
            }
        }

        Assert.Empty(VulkanDevice.ValidationMessages);
    }

    [Fact]
    public async Task LutSamplingMatchesTheCpu()
    {
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        using var library = new ShaderLibrary(device);
        using var scheduler = new GpuScheduler(device);
        var lut = CubeLut.TryParse(TestLuts.Cube(9, rgb => (Math.Pow(rgb.R, 0.7), rgb.G * rgb.B, 1 - rgb.B)).Split('\n'))!;
        var random = new Random(5);
        var colors = Enumerable.Range(0, 1000).Select(_ => new Vector3(random.NextSingle() * 1.2f - 0.1f, random.NextSingle(), random.NextSingle())).ToArray();

        var tableData = lut.Table.ToArray().SelectMany(c => new[] { c.X, c.Y, c.Z }).ToArray();
        var inputData = colors.SelectMany(c => new[] { c.X, c.Y, c.Z }).ToArray();
        using var table = Upload(allocator, tableData);
        using var input = Upload(allocator, inputData);
        using var output = allocator.CreateBuffer((ulong)(inputData.Length * sizeof(float)), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Readback);
        var pipeline = library.Get(typeof(ShaderTests).Assembly, "test_lut",
            [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer], Marshal.SizeOf<LutParams>());
        var parameters = new LutParams(lut.DomainMin, lut.Size, lut.DomainScale, colors.Length);

        var fence = await scheduler.RunAsync(context =>
        {
            pipeline.Dispatch(context, [table, input, output], parameters, ComputePipeline.Groups(colors.Length, 64), 1);
            GpuBarriers.AfterCompute(context);
            return context.Fence;
        }, TestContext.Current.CancellationToken);
        fence.Wait(TestContext.Current.CancellationToken);
        output.Invalidate();

        var gpu = MemoryMarshal.Cast<byte, float>(output.Span).ToArray();
        for (var i = 0; i < colors.Length; i++)
        {
            var cpu = lut.Sample(colors[i]);
            var actual = new Vector3(gpu[i * 3], gpu[i * 3 + 1], gpu[i * 3 + 2]);
            Assert.True(Vector3.Distance(cpu, actual) < 1e-5f, $"{colors[i]}: CPU {cpu} と GPU {actual} が違う");
        }
    }

    private static GpuBuffer Upload(GpuAllocator allocator, float[] data)
    {
        var buffer = allocator.CreateBuffer((ulong)(data.Length * sizeof(float)), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Upload);
        MemoryMarshal.AsBytes(data.AsSpan()).CopyTo(buffer.Span);
        buffer.Flush();
        return buffer;
    }
}
