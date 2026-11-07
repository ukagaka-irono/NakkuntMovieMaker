using NakkuntMovieMaker.Media;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>画像のレイアウトの切り替え（パイプラインバリア）</summary>
public static unsafe class GpuBarriers
{
    /// <summary>
    /// image のレイアウトを layout に切り替える。それまでのすべての書き込みが終わってから、後のすべての読み書きができるようにする
    /// （細かく分けずに、全体を待つ。描画の 1 フレームの中の切り替えの数は少ないので、十分に速い）
    /// </summary>
    public static void Transition(GpuCommandContext context, GpuImage image, VkImageLayout layout)
    {
        if (image.Layout == layout && layout != VkImageLayout.General)
        {
            return;
        }

        var barrier = new VkImageMemoryBarrier
        {
            srcAccessMask = VkAccessFlags.MemoryWrite,
            dstAccessMask = VkAccessFlags.MemoryRead | VkAccessFlags.MemoryWrite,
            oldLayout = image.Layout,
            newLayout = layout,
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            image = image.Image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        context.Device.DeviceApi.vkCmdPipelineBarrier(
            context.CommandBuffer, VkPipelineStageFlags.AllCommands, VkPipelineStageFlags.AllCommands, 0, 0, null, 0, null, 1, &barrier);
        image.Layout = layout;
    }

    /// <summary>コンピュートシェーダーの書き込みが終わってから、後のシェーダー・転送・CPU が読めるようにする</summary>
    public static void AfterCompute(GpuCommandContext context)
    {
        var barrier = new VkMemoryBarrier
        {
            srcAccessMask = VkAccessFlags.ShaderWrite,
            dstAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite | VkAccessFlags.TransferRead | VkAccessFlags.HostRead,
        };
        context.Device.DeviceApi.vkCmdPipelineBarrier(
            context.CommandBuffer,
            VkPipelineStageFlags.ComputeShader,
            VkPipelineStageFlags.ComputeShader | VkPipelineStageFlags.Transfer | VkPipelineStageFlags.Host,
            0, 1, &barrier, 0, null, 0, null);
    }

    /// <summary>バッファーへの書き込み（転送）が終わってから、CPU が読めるようにする</summary>
    public static void TransferToHost(GpuCommandContext context)
    {
        var barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.HostRead };
        context.Device.DeviceApi.vkCmdPipelineBarrier(
            context.CommandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, 0, 1, &barrier, 0, null, 0, null);
    }
}

/// <summary>
/// CPU と GPU の間で画像を運ぶ。アップロードは、書き込み用のバッファーに写してから画像に転送し、
/// 読み戻しは、画像を読み戻し用のバッファーに転送してから、GPU で終わるのを待って読む
/// </summary>
public static unsafe class GpuTransfers
{
    /// <summary>
    /// premultiplied の BGRA の画像を、GPU の B8G8R8A8_UNORM の画像にする（GPU のスレッドで記録する）。
    /// 返す画像はシェーダーから読めるレイアウト。使い終わったら破棄する（書き込み用のバッファーは、仕事が終わったら捨てる）
    /// </summary>
    public static GpuImage Upload(GpuCommandContext context, GpuAllocator allocator, BgraImage image, VkImageUsageFlags extraUsage = 0)
    {
        var target = allocator.CreateImage(image.Width, image.Height, VkFormat.B8G8R8A8Unorm,
            VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc | extraUsage);
        var staging = allocator.CreateBuffer((ulong)image.Pixels.Length, VkBufferUsageFlags.TransferSrc, GpuBufferKind.Upload);
        image.Pixels.CopyTo(staging.Span);
        staging.Flush();

        GpuBarriers.Transition(context, target, VkImageLayout.TransferDstOptimal);
        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new VkExtent3D((uint)image.Width, (uint)image.Height, 1),
        };
        context.Device.DeviceApi.vkCmdCopyBufferToImage(context.CommandBuffer, staging.Buffer, target.Image, VkImageLayout.TransferDstOptimal, 1, &region);
        GpuBarriers.Transition(context, target, VkImageLayout.ShaderReadOnlyOptimal);
        context.OnCompleted(staging.Dispose);
        return target;
    }

    /// <summary>
    /// B8G8R8A8_UNORM の画像を読み戻す記録をする（GPU のスレッドで）。返したものの Read で、GPU で終わるのを待って BGRA の画像を受け取る
    /// </summary>
    public static GpuReadback Readback(GpuCommandContext context, GpuAllocator allocator, GpuImage image)
    {
        if (image.Format != VkFormat.B8G8R8A8Unorm)
        {
            throw new ArgumentException($"読み戻せるのは B8G8R8A8_UNORM の画像だけです（{image.Format}）", nameof(image));
        }

        var buffer = allocator.CreateBuffer((ulong)(image.Width * image.Height * 4), VkBufferUsageFlags.TransferDst, GpuBufferKind.Readback);
        GpuBarriers.Transition(context, image, VkImageLayout.TransferSrcOptimal);
        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new VkExtent3D((uint)image.Width, (uint)image.Height, 1),
        };
        context.Device.DeviceApi.vkCmdCopyImageToBuffer(context.CommandBuffer, image.Image, VkImageLayout.TransferSrcOptimal, buffer.Buffer, 1, &region);
        GpuBarriers.TransferToHost(context);
        return new GpuReadback(context.Fence, buffer, image.Width, image.Height);
    }
}

/// <summary>読み戻しの途中。Read で GPU の完了を待って画像を受け取る（1 回だけ）。読まずに破棄してもよい</summary>
public sealed class GpuReadback(GpuFence fence, GpuBuffer buffer, int width, int height) : IDisposable
{
    private int _done;

    public int Width => width;

    public int Height => height;

    public BgraImage Read(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _done) != 0, this);
        fence.Wait(cancellationToken);
        buffer.Invalidate();
        var pixels = buffer.Span.ToArray();
        Dispose();
        return new BgraImage(width, height, pixels);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0)
        {
            // GPU がまだ書いているかもしれないので、終わるのを待ってから捨てる
            if (!fence.IsCompleted)
            {
                fence.Wait();
            }

            buffer.Dispose();
        }
    }
}
