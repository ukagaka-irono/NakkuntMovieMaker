using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>GPU のメモリの確保・使い回し・キャッシュ</summary>
public sealed class GpuMemoryTests
{
    private const VkImageUsageFlags Usage = VkImageUsageFlags.Sampled | VkImageUsageFlags.Storage | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst;

    [Fact]
    public void CreatesAndDestroysImagesAndBuffers()
    {
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        Assert.InRange(allocator.BudgetBytes, 1L << 20, 1L << 30);

        var image = allocator.CreateImage(320, 180, VkFormat.R16G16B16A16Sfloat, Usage);
        using (var upload = allocator.CreateBuffer(4096, VkBufferUsageFlags.TransferSrc, GpuBufferKind.Upload))
        using (var readback = allocator.CreateBuffer(4096, VkBufferUsageFlags.TransferDst, GpuBufferKind.Readback))
        {
            Assert.Equal((320, 180), (image.Width, image.Height));
            Assert.True(image.Bytes >= 320 * 180 * 8);
            Assert.Equal(4096, upload.Span.Length);
            Assert.Equal(4096, readback.Span.Length);
            upload.Span.Fill(7);
            upload.Flush();
            Assert.Equal(3, allocator.LiveAllocations);
        }

        image.Dispose();
        image.Dispose(); // 2 回破棄しても問題ない
        Assert.Equal((0L, 0L), (allocator.LiveAllocations, allocator.AllocatedBytes));
    }

    [Fact]
    public async Task ReusesTransientImagesAfterTheGpuFinishes()
    {
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        using (var scheduler = new GpuScheduler(device))
        using (var pool = new TexturePool(allocator, 64L << 20))
        {
            var first = await scheduler.RunAsync(context =>
            {
                var image = pool.Rent(64, 64, VkFormat.R16G16B16A16Sfloat, Usage);
                pool.Return(context, image);
                return image;
            }, TestContext.Current.CancellationToken);

            // 返した画像は、次の次の仕事（記録の場所を使い回すとき）までに使い回せるようになる
            for (var i = 0; i < 3; i++)
            {
                await scheduler.RunAsync(_ => 0, TestContext.Current.CancellationToken);
            }

            var second = await scheduler.RunAsync(_ => pool.Rent(64, 64, VkFormat.R16G16B16A16Sfloat, Usage), TestContext.Current.CancellationToken);
            Assert.Same(first, second);

            // 大きさや形式が違えば、別の画像
            var other = await scheduler.RunAsync(_ => pool.Rent(64, 32, VkFormat.R16G16B16A16Sfloat, Usage), TestContext.Current.CancellationToken);
            Assert.NotSame(first, other);
            second.Dispose();
            other.Dispose();
        }

        Assert.Equal(0, allocator.LiveAllocations);
    }

    [Fact]
    public void EvictsTheLeastRecentlyUsedAndDisposesWhenTheGpuIsDone()
    {
        using var device = Gpu.CreateOrSkip();
        using var allocator = new GpuAllocator(device);
        var image = allocator.CreateImage(16, 16, VkFormat.R16G16B16A16Sfloat, Usage);
        var bytes = image.Bytes;
        image.Dispose();

        using (var cache = new GpuCache<string>(bytes * 2))
        {
            cache.Add("a", allocator.CreateImage(16, 16, VkFormat.R16G16B16A16Sfloat, Usage), bytes, use: 1);
            cache.Add("b", allocator.CreateImage(16, 16, VkFormat.R16G16B16A16Sfloat, Usage), bytes, use: 2);
            Assert.True(cache.TryGet<GpuImage>("a", use: 3, out _)); // a を使ったので、b のほうが古い
            cache.Add("c", allocator.CreateImage(16, 16, VkFormat.R16G16B16A16Sfloat, Usage), bytes, use: 4);

            Assert.False(cache.TryGet<GpuImage>("b", 5, out _));
            Assert.True(cache.TryGet<GpuImage>("a", 5, out _));
            Assert.True(cache.TryGet<GpuImage>("c", 5, out _));
            Assert.Equal((2, bytes * 2), (cache.Count, cache.Bytes));

            // b を最後に使った仕事（2）が終わるまでは破棄しない
            Assert.Equal(1, cache.RetiredCount);
            cache.Collect(completed: 1);
            Assert.Equal((1, 3L), (cache.RetiredCount, allocator.LiveAllocations));
            cache.Collect(completed: 2);
            Assert.Equal((0, 2L), (cache.RetiredCount, allocator.LiveAllocations));
        }

        Assert.Equal(0, allocator.LiveAllocations);
    }
}
