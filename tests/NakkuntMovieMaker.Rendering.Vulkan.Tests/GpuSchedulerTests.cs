using System.Collections.Concurrent;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>GPU のスレッドと、タイムラインセマフォでの完了の待ち合わせ</summary>
public sealed class GpuSchedulerTests
{
    [Fact]
    public async Task RunsJobsFromManyThreadsInOrder()
    {
        VulkanDevice.ClearValidationMessages();
        using var device = Gpu.CreateOrSkip();
        using var buffer = new HostBuffer(device, 1500 * sizeof(uint));
        using (var scheduler = new GpuScheduler(device))
        {
            // 3 つのスレッドから 500 個ずつ。仕事ごとに、自分の番号をバッファーの決まった場所に書く
            var fences = new ConcurrentBag<(int Job, GpuFence Fence)>();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(thread => Task.Run(async () =>
            {
                for (var i = 0; i < 500; i++)
                {
                    var job = thread * 500 + i;
                    var fence = await scheduler.RunAsync(context =>
                    {
                        Assert.True(scheduler.IsGpuThread);
                        context.Device.DeviceApi.vkCmdFillBuffer(context.CommandBuffer, buffer.Handle, (ulong)job * sizeof(uint), sizeof(uint), (uint)job + 1);
                        return context.Fence;
                    }, TestContext.Current.CancellationToken);
                    fences.Add((job, fence));
                }
            }, TestContext.Current.CancellationToken)));

            // 値は仕事ごとに 1 つずつ進み、重ならない
            var values = fences.Select(entry => entry.Fence.Value).Order().ToList();
            Assert.Equal(Enumerable.Range(1, 1500).Select(value => (ulong)value), values);

            fences.MaxBy(entry => entry.Fence.Value).Fence.Wait(TestContext.Current.CancellationToken);
            Assert.All(fences, entry => Assert.True(entry.Fence.IsCompleted));
            Assert.Equal(1500UL, scheduler.CompletedValue);
        }

        var written = buffer.Read<uint>(1500);
        Assert.Equal(Enumerable.Range(1, 1500).Select(value => (uint)value), written);
        Assert.Empty(VulkanDevice.ValidationMessages);
    }

    [Fact]
    public async Task ReportsFailedRecordingWithoutSubmitting()
    {
        using var device = Gpu.CreateOrSkip();
        using var scheduler = new GpuScheduler(device);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync<int>(_ => throw new InvalidOperationException("記録の失敗"), TestContext.Current.CancellationToken));
        var fence = await scheduler.RunAsync(context => context.Fence, TestContext.Current.CancellationToken);

        // 失敗した仕事は値を進めない
        Assert.Equal(1UL, fence.Value);
        fence.Wait(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunsCompletionsAfterTheGpuFinishes()
    {
        using var device = Gpu.CreateOrSkip();
        var completed = new ConcurrentQueue<ulong>();
        using (var scheduler = new GpuScheduler(device, framesInFlight: 2))
        {
            for (var i = 0; i < 10; i++)
            {
                await scheduler.RunAsync(context =>
                {
                    var value = context.Value;
                    context.OnCompleted(() =>
                    {
                        // 呼ばれるのは、その仕事が GPU で終わってから
                        Assert.True(scheduler.CompletedValue >= value);
                        completed.Enqueue(value);
                    });
                    return 0;
                }, TestContext.Current.CancellationToken);
            }
        }

        // 破棄するまでに、すべての後始末が呼ばれる
        Assert.Equal(Enumerable.Range(1, 10).Select(value => (ulong)value), completed.Order());
    }

    [Fact]
    public async Task CancelsJobsThatHaveNotStarted()
    {
        using var device = Gpu.CreateOrSkip();
        using var scheduler = new GpuScheduler(device);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(_ => 1, cancel.Token));
    }

    // CPU から読めるバッファー（テスト用）
    private sealed unsafe class HostBuffer : IDisposable
    {
        private readonly VulkanDevice _device;
        private readonly VkDeviceMemory _memory;
        private readonly ulong _size;

        public HostBuffer(VulkanDevice device, ulong size)
        {
            _device = device;
            _size = size;
            var api = device.DeviceApi;
            var info = new VkBufferCreateInfo { size = size, usage = VkBufferUsageFlags.TransferDst, sharingMode = VkSharingMode.Exclusive };
            VkBuffer buffer;
            Assert.Equal(VkResult.Success, api.vkCreateBuffer(&info, null, &buffer));
            Handle = buffer;
            VkMemoryRequirements requirements;
            api.vkGetBufferMemoryRequirements(buffer, &requirements);
            VkPhysicalDeviceMemoryProperties properties;
            device.InstanceApi.vkGetPhysicalDeviceMemoryProperties(device.PhysicalDevice, &properties);
            const VkMemoryPropertyFlags Wanted = VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent;
            var type = -1;
            for (var i = 0; i < properties.memoryTypeCount && type < 0; i++)
            {
                if ((requirements.memoryTypeBits & (1u << i)) != 0 && (properties.memoryTypes[i].propertyFlags & Wanted) == Wanted)
                {
                    type = i;
                }
            }

            var allocate = new VkMemoryAllocateInfo { allocationSize = requirements.size, memoryTypeIndex = (uint)type };
            VkDeviceMemory memory;
            Assert.Equal(VkResult.Success, api.vkAllocateMemory(&allocate, null, &memory));
            _memory = memory;
            Assert.Equal(VkResult.Success, api.vkBindBufferMemory(buffer, memory, 0));
        }

        public VkBuffer Handle { get; }

        public T[] Read<T>(int count)
            where T : unmanaged
        {
            void* data;
            Assert.Equal(VkResult.Success, _device.DeviceApi.vkMapMemory(_memory, 0, _size, 0, &data));
            var values = new ReadOnlySpan<T>(data, count).ToArray();
            _device.DeviceApi.vkUnmapMemory(_memory);
            return values;
        }

        public void Dispose()
        {
            _device.DeviceApi.vkDestroyBuffer(Handle, null);
            _device.DeviceApi.vkFreeMemory(_memory, null);
        }
    }
}
