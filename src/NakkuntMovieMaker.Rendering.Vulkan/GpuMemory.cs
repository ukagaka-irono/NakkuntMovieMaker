using Vortice.Vulkan;
using static Vortice.Vulkan.Vma;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>バッファーの使い方。Upload は CPU から書いて GPU に送る、Readback は GPU の結果を CPU で読む（どちらも常にマップしておく）</summary>
public enum GpuBufferKind
{
    DeviceLocal,
    Upload,
    Readback,
}

/// <summary>
/// GPU のメモリの確保（Vulkan Memory Allocator）。画像とバッファーを作り、確保している量を数える。
/// 作ったものは、GPU で使い終わってから破棄すること（GpuCommandContext.OnCompleted で破棄するか、GpuCache に任せる）
/// </summary>
public sealed unsafe class GpuAllocator : IDisposable
{
    private readonly VmaAllocator _allocator;
    private long _liveAllocations;
    private long _allocatedBytes;
    private bool _disposed;

    /// <param name="budgetBytes">使ってよい GPU のメモリ（null なら、ヒープの大きさから決める既定値）</param>
    public GpuAllocator(VulkanDevice device, long? budgetBytes = null)
    {
        Device = device;
        var functions = new VmaVulkanFunctions
        {
            vkGetInstanceProcAddr = Vortice.Vulkan.Vulkan.vkGetInstanceProcAddr_ptr,
            vkGetDeviceProcAddr = (delegate* unmanaged<VkDevice, byte*, PFN_vkVoidFunction>)device.GetProcAddress("vkGetDeviceProcAddr", device.Instance.Handle, 0),
        };
        var info = new VmaAllocatorCreateInfo
        {
            physicalDevice = device.PhysicalDevice,
            device = device.Device,
            instance = device.Instance,
            vulkanApiVersion = VkVersion.Version_1_2,
            pVulkanFunctions = &functions,
        };
        var result = vmaCreateAllocator(&info, out var allocator);
        if (result != VkResult.Success)
        {
            throw new VulkanException($"GPU のメモリを準備できません（{result}）");
        }

        _allocator = allocator;
        BudgetBytes = budgetBytes ?? DefaultBudget(device);
    }

    public VulkanDevice Device { get; }

    /// <summary>まだ破棄していない画像とバッファーの数（テストで、破棄し忘れがないかを確かめる）</summary>
    public long LiveAllocations => Interlocked.Read(ref _liveAllocations);

    /// <summary>確保している量（バイト）</summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

    /// <summary>キャッシュに使ってよい量の目安（GPU のメモリの半分、最大 1GB。内蔵の GPU はメインメモリと共有なので控えめにする）</summary>
    public long BudgetBytes { get; }

    /// <summary>2D の画像を作る（ミップマップなし、最適な並び、最初は Undefined のレイアウト）</summary>
    public GpuImage CreateImage(int width, int height, VkFormat format, VkImageUsageFlags usage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var imageInfo = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D((uint)width, (uint)height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = usage,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        var allocationInfo = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice };
        VmaAllocationInfo allocated;
        var result = vmaCreateImage(_allocator, in imageInfo, in allocationInfo, out var image, out var allocation, &allocated);
        if (result != VkResult.Success)
        {
            throw new VulkanException($"GPU に画像（{width}×{height}）を作れません（{result}）");
        }

        var view = CreateView(image, format);
        Track(+1, (long)allocated.size);
        return new GpuImage(this, image, view, allocation, width, height, format, usage, (long)allocated.size);
    }

    public GpuBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, GpuBufferKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bufferInfo = new VkBufferCreateInfo { size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        var allocationInfo = kind switch
        {
            GpuBufferKind.Upload => new VmaAllocationCreateInfo
            {
                usage = VmaMemoryUsage.Auto,
                flags = VmaAllocationCreateFlags.HostAccessSequentialWrite | VmaAllocationCreateFlags.Mapped,
            },
            GpuBufferKind.Readback => new VmaAllocationCreateInfo
            {
                usage = VmaMemoryUsage.Auto,
                flags = VmaAllocationCreateFlags.HostAccessRandom | VmaAllocationCreateFlags.Mapped,
            },
            _ => new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice },
        };
        VmaAllocationInfo allocated;
        var result = vmaCreateBuffer(_allocator, in bufferInfo, in allocationInfo, out var buffer, out var allocation, &allocated);
        if (result != VkResult.Success)
        {
            throw new VulkanException($"GPU にバッファー（{size} バイト）を作れません（{result}）");
        }

        Track(+1, (long)allocated.size);
        return new GpuBuffer(this, buffer, allocation, size, (long)allocated.size, allocated.pMappedData);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            vmaDestroyAllocator(_allocator);
        }
    }

    internal void Destroy(GpuImage image)
    {
        Device.DeviceApi.vkDestroyImageView(image.View, null);
        vmaDestroyImage(_allocator, image.Image, image.Allocation);
        Track(-1, -image.Bytes);
    }

    internal void Destroy(GpuBuffer buffer)
    {
        vmaDestroyBuffer(_allocator, buffer.Buffer, buffer.Allocation);
        Track(-1, -buffer.Bytes);
    }

    /// <summary>読み戻し用のバッファーを、CPU から読む前に最新にする（キャッシュされたメモリのとき）</summary>
    internal void Invalidate(GpuBuffer buffer) => vmaInvalidateAllocation(_allocator, buffer.Allocation, 0, buffer.Size);

    /// <summary>書き込み用のバッファーを、GPU が読む前に書き出す（コヒーレントでないメモリのとき）</summary>
    internal void Flush(GpuBuffer buffer) => vmaFlushAllocation(_allocator, buffer.Allocation, 0, buffer.Size);

    private void Track(int count, long bytes)
    {
        Interlocked.Add(ref _liveAllocations, count);
        Interlocked.Add(ref _allocatedBytes, bytes);
    }

    private VkImageView CreateView(VkImage image, VkFormat format)
    {
        var info = new VkImageViewCreateInfo
        {
            image = image,
            viewType = VkImageViewType.Image2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        VkImageView view;
        var result = Device.DeviceApi.vkCreateImageView(&info, null, &view);
        if (result != VkResult.Success)
        {
            throw new VulkanException($"画像のビューを作れません（{result}）");
        }

        return view;
    }

    private static long DefaultBudget(VulkanDevice device)
    {
        VkPhysicalDeviceMemoryProperties properties;
        device.InstanceApi.vkGetPhysicalDeviceMemoryProperties(device.PhysicalDevice, &properties);
        var largest = 0UL;
        for (var i = 0; i < properties.memoryHeapCount; i++)
        {
            var heap = properties.memoryHeaps[i];
            if ((heap.flags & VkMemoryHeapFlags.DeviceLocal) != 0)
            {
                largest = Math.Max(largest, heap.size);
            }
        }

        return Math.Min((long)(largest / 2), 1L << 30);
    }
}

/// <summary>GPU の 2D の画像とビュー。Layout は、最後に記録したバリアの後のレイアウト（記録する側が更新する）</summary>
public sealed class GpuImage : IDisposable
{
    private readonly GpuAllocator _allocator;
    private int _disposed;

    internal GpuImage(GpuAllocator allocator, VkImage image, VkImageView view, VmaAllocation allocation, int width, int height, VkFormat format, VkImageUsageFlags usage, long bytes)
    {
        Usage = usage;
        _allocator = allocator;
        Image = image;
        View = view;
        Allocation = allocation;
        Width = width;
        Height = height;
        Format = format;
        Bytes = bytes;
    }

    public VkImage Image { get; }

    public VkImageView View { get; }

    internal VmaAllocation Allocation { get; }

    public int Width { get; }

    public int Height { get; }

    public VkFormat Format { get; }

    public VkImageUsageFlags Usage { get; }

    public long Bytes { get; }

    public VkImageLayout Layout { get; set; } = VkImageLayout.Undefined;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocator.Destroy(this);
        }
    }
}

/// <summary>GPU のバッファー。Upload・Readback のときは Mapped から読み書きできる</summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly GpuAllocator _allocator;
    private int _disposed;

    internal GpuBuffer(GpuAllocator allocator, VkBuffer buffer, VmaAllocation allocation, ulong size, long bytes, void* mapped)
    {
        _allocator = allocator;
        Buffer = buffer;
        Allocation = allocation;
        Size = size;
        Bytes = bytes;
        Mapped = mapped;
    }

    public VkBuffer Buffer { get; }

    internal VmaAllocation Allocation { get; }

    public ulong Size { get; }

    public long Bytes { get; }

    /// <summary>CPU から見たバッファーの先頭（DeviceLocal なら null）</summary>
    public void* Mapped { get; }

    public Span<byte> Span => Mapped is null ? [] : new Span<byte>(Mapped, checked((int)Size));

    /// <summary>GPU が書いた内容を読む前に呼ぶ</summary>
    public void Invalidate() => _allocator.Invalidate(this);

    /// <summary>CPU で書いた内容を GPU に読ませる前に呼ぶ</summary>
    public void Flush() => _allocator.Flush(this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocator.Destroy(this);
        }
    }
}
