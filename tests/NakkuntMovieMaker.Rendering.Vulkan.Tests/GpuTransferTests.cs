using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>CPU と GPU の間で画像を運ぶ</summary>
public sealed class GpuTransferTests
{
    [Fact]
    public async Task UploadsAndReadsBackTheSamePixels()
    {
        VulkanDevice.ClearValidationMessages();
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        var original = TestImages.Subject(123, 45); // 幅が 4 の倍数でない画像
        using (var scheduler = new GpuScheduler(device))
        {
            using var readback = await scheduler.RunAsync(context =>
            {
                var image = GpuTransfers.Upload(context, allocator, original);
                var result = GpuTransfers.Readback(context, allocator, image);
                context.OnCompleted(image.Dispose);
                return result;
            }, TestContext.Current.CancellationToken);

            var copy = readback.Read(TestContext.Current.CancellationToken);

            Assert.Equal((original.Width, original.Height), (copy.Width, copy.Height));
            Assert.Equal(original.Pixels, copy.Pixels);
        }

        Assert.Equal(0, allocator.LiveAllocations);
        Assert.Empty(VulkanDevice.ValidationMessages);
    }

    [Fact]
    public async Task ReadsBackManyFramesInFlight()
    {
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        using var scheduler = new GpuScheduler(device);
        var frames = Enumerable.Range(0, 12).Select(i => TestImages.Solid(64, 32, ((byte)(i * 20), 0, (byte)(255 - i * 20), 255))).ToList();

        // 読み戻しを待たずに次々に送り、あとでまとめて読む
        var pending = new List<GpuReadback>();
        foreach (var frame in frames)
        {
            pending.Add(await scheduler.RunAsync(context =>
            {
                var image = GpuTransfers.Upload(context, allocator, frame);
                context.OnCompleted(image.Dispose);
                return GpuTransfers.Readback(context, allocator, image);
            }, TestContext.Current.CancellationToken));
        }

        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Pixels, pending[i].Read(TestContext.Current.CancellationToken).Pixels);
        }
    }
}
