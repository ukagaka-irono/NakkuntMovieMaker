using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// FFmpeg に、描画と同じ Vulkan のデバイスでデコードさせる（F-31。試験的な機能で、デバイスを VideoDecode にしたときだけ使う）。
/// samples/general の clip.mp4（OpenH264 の Constrained Baseline）は、FFmpeg 9.0.2 と基準の PC の Intel のドライバーでは、
/// 2 つ目のキーフレームをデコードしたところで落ちる（ffmpeg コマンドの -hwaccel vulkan でも同じ）ので、ここでは使わない
/// </summary>
public sealed unsafe class FfmpegVulkanTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void OffsetsMatchFfmpegsOwnDevice()
    {
        // FFmpeg が自分で作った Vulkan のデバイスを、こちらのオフセットで読んで、値が正しく見えることを確かめる
        Assert.SkipWhen(!FfmpegLibrary.TryLoad(out _, out var error), $"FFmpeg を読み込めないためスキップします（{error}）");
        AVBufferRef* reference = null;
        var created = ffmpeg.av_hwdevice_ctx_create(&reference, AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN, null, null, 0);
        Assert.SkipWhen(created < 0, $"FFmpeg が Vulkan のデバイスを作れないためスキップします（{created}）");
        try
        {
            var hw = (byte*)((AVHWDeviceContext*)reference->data)->hwctx;
            Assert.NotEqual(0, *(nint*)(hw + FfmpegVulkanDevice.GetProcAddr));
            Assert.NotEqual(0, *(nint*)(hw + FfmpegVulkanDevice.Instance));
            Assert.NotEqual(0, *(nint*)(hw + FfmpegVulkanDevice.PhysicalDevice));
            Assert.NotEqual(0, *(nint*)(hw + FfmpegVulkanDevice.Device));
            Assert.Equal(1000059000, *(int*)(hw + FfmpegVulkanDevice.DeviceFeatures)); // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2
            var familyCount = *(int*)(hw + FfmpegVulkanDevice.QueueFamilyCount);
            Assert.InRange(familyCount, 1, 64);
            var families = (int*)(hw + FfmpegVulkanDevice.QueueFamilies);
            for (var i = 0; i < familyCount; i++)
            {
                Assert.InRange(families[i * 4 + 1], 1, 64); // キューの数
                Assert.NotEqual(0, families[i * 4 + 2]); // できること
            }

            Assert.NotEqual(0, *(nint*)(hw + FfmpegVulkanDevice.LockQueueCallback));
            Assert.InRange(*(int*)(hw + FfmpegVulkanDevice.DeviceExtensionCount), 1, 256);
        }
        finally
        {
            ffmpeg.av_buffer_unref(&reference);
        }
    }

    [Fact]
    public void VideoDecodeIsOffByDefault()
    {
        // 既定では、Vulkan Video のデコードのキューを作らない（描画はできる）
        using var device = Gpu.CreateOrSkip(Gpu.Options with { VideoDecode = false });
        Assert.Null(device.VideoDecode);
        Assert.Null(FfmpegVulkanDevice.TryCreate(device, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void DecodesOnTheRenderingDevice()
    {
        Gpu.RequireVulkanVideoTests();
        using var device = Gpu.CreateOrSkip();
        Assert.SkipWhen(device.VideoDecode is null, "GPU が Vulkan Video のデコードに対応していないためスキップします");
        using var video = FfmpegVulkanDevice.TryCreate(device, out var problem);
        Assert.True(video is not null, problem);

        using var decoder = VideoDecoder.Open(Fixture("frames-2997.mp4"), new VideoDecoderOptions(Output: VideoFrameOutput.Gpu, VulkanDevice: video.Reference, CacheCapacity: 2));
        foreach (var time in new[] { Rational.Zero, new Rational(1, 2), new Rational(1, 10) })
        {
            var frame = decoder.GetFrame(time);
            Assert.NotNull(frame.GpuFrame);
            Assert.Equal(VideoPlaneFormat.Nv12, frame.GpuFrame.Format);
        }

        Assert.Equal("vulkan", decoder.HardwareDevice);
    }

    [Fact]
    public void FallsBackToPlanesWhenTheGpuCannotDecode()
    {
        Gpu.RequireVulkanVideoTests();
        // GPU が Vulkan Video でデコードできない動画（基準の PC の Iris Xe のドライバーは HEVC Main 10 を Vulkan でデコードできない）は、
        // FFmpeg がソフトウェアでデコードし、平面のまま渡す。デコードできる GPU なら、GPU のフレームのまま渡す
        using var device = Gpu.CreateOrSkip();
        Assert.SkipWhen(device.VideoDecode is null, "GPU が Vulkan Video のデコードに対応していないためスキップします");
        using var video = FfmpegVulkanDevice.TryCreate(device, out var problem);
        Assert.True(video is not null, problem);

        using var decoder = VideoDecoder.Open(Fixture("hevc-main10-320x240.mp4"), new VideoDecoderOptions(Output: VideoFrameOutput.Gpu, VulkanDevice: video.Reference, CacheCapacity: 2));
        var frame = decoder.GetFrame(Rational.Zero);

        if (frame.GpuFrame is { } gpu)
        {
            Assert.Equal(VideoPlaneFormat.P010, gpu.Format);
        }
        else
        {
            Assert.NotNull(frame.Planes);
            Assert.Equal(VideoPlaneFormat.P010, frame.Planes.Format);
        }
    }
}
