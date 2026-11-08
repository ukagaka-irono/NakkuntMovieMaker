using System.Runtime.Versioning;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

/// <summary>
/// Windows の d3d11va でデコードした画像を、共有のテクスチャに写して渡す（K-2）。
/// 描き方（Vulkan に取り込んで、写して送る流れと同じ絵になるか）は Rendering.Vulkan.Tests の GpuVideoTests で確かめる
/// </summary>
public sealed class D3D11VideoDeviceTests
{
    [Theory]
    [InlineData("hevc-main10-320x240.mp4", VideoPlaneFormat.P010)]
    [InlineData("frames-2997.mp4", VideoPlaneFormat.Nv12)]
    [SupportedOSPlatform("windows")]
    public void ReusesSharedTexturesForEveryFrame(string file, VideoPlaneFormat format)
    {
        // デコーダーが覚えきれなくなったフレームは捨てられ、共有のテクスチャが返るので、何枚デコードしても足りなくならない
        Assert.SkipUnless(OperatingSystem.IsWindows(), "d3d11va は Windows だけです");
        Ffmpeg.RequireOrSkip();
        using var device = D3D11VideoDevice.TryCreate(null, out var problem);
        Assert.SkipWhen(device is null, $"d3d11va のデバイスを作れないためスキップします（{problem}）");
        using var decoder = VideoDecoder.Open(Ffmpeg.Fixture(file), new VideoDecoderOptions(CacheCapacity: 4, Output: VideoFrameOutput.Gpu, D3D11: device));
        Assert.Equal("d3d11va", decoder.HardwareDevice);

        var ids = new HashSet<int>();
        for (var frame = 0; frame < 3 * D3D11VideoDevice.MaxTextures; frame++)
        {
            var decoded = decoder.GetFrame(new Rational(frame, 30));
            var gpu = Assert.IsType<HardwareVideoFrame>(decoded.GpuFrame);
            var shared = Assert.IsType<D3D11SharedTexture>(gpu.SharedTexture);
            Assert.Equal(format, gpu.Format);
            Assert.True(shared.Width >= gpu.Width && shared.Height >= gpu.Height);
            Assert.NotEqual(0, shared.Handle);
            ids.Add(shared.Id);

            // 別の場所で使う参照は、捨てるまでテクスチャを借りたまま
            using var clone = gpu.Clone();
            Assert.Same(shared, clone.SharedTexture);
        }

        Assert.True(ids.Count < D3D11VideoDevice.MaxTextures, $"共有のテクスチャを {ids.Count} 枚使いました（返っていません）");
    }
}
