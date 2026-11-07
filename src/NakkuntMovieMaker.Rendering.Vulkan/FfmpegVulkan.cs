using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Media;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// 描画と同じ Vulkan のデバイスを、FFmpeg の Vulkan のデバイス（AVHWDeviceContext）にしたもの。
/// FFmpeg はこのデバイスの Vulkan Video でデコードし、デコードしたフレーム（VkImage）を、描画がコピーせずに読む。
/// グラフィックスのキューは描画と FFmpeg で共有するので、送るときの錠（VulkanDevice.QueueLock）を FFmpeg にも取らせる
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen には libavutil/hwcontext_vulkan.h の構造体がないので、x64 のオフセットで読み書きする
/// （FFmpeg 9.0.2・libavutil 61 のヘッダーを gcc でコンパイルして確かめた値。FfmpegVulkanTests で、FFmpeg が作ったデバイスを読んで確かめる）
/// </remarks>
public sealed unsafe class FfmpegVulkanDevice : HardwareVideoDevice, IDisposable
{
    // AVVulkanDeviceContext のオフセット
    internal const int GetProcAddr = 8;
    internal const int Instance = 16;
    internal const int PhysicalDevice = 24;
    internal const int Device = 32;
    internal const int DeviceFeatures = 40;
    internal const int InstanceExtensions = 280;
    internal const int InstanceExtensionCount = 288;
    internal const int DeviceExtensions = 296;
    internal const int DeviceExtensionCount = 304;
    internal const int LockQueueCallback = 312;
    internal const int UnlockQueueCallback = 320;
    internal const int QueueFamilies = 328;
    internal const int QueueFamilyCount = 1352;
    internal const int QueueFamilySize = 16;

    private AVBufferRef* _reference;
    private GCHandle _owner;

    private FfmpegVulkanDevice(AVBufferRef* reference, GCHandle owner)
    {
        _reference = reference;
        _owner = owner;
    }

    /// <summary>FFmpeg のデバイス（AVBufferRef*）。VideoDecoderOptions.VulkanDevice に渡す</summary>
    public override nint Reference => (nint)_reference;

    /// <summary>
    /// FFmpeg のデバイスを作る。Vulkan Video のデコードに対応していない・FFmpeg が Vulkan に対応していない・作れないときは null と理由
    /// </summary>
    public static FfmpegVulkanDevice? TryCreate(VulkanDevice device, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.VideoDecode is not { } video)
        {
            problem = "GPU が Vulkan Video のデコードに対応していません";
            return null;
        }

        if (!FfmpegLibrary.TryLoad(out _, out var ffmpegError))
        {
            problem = ffmpegError;
            return null;
        }

        if (VulkanLoaderEntry() is not { } getInstanceProcAddr)
        {
            problem = "Vulkan のローダーの vkGetInstanceProcAddr が見つかりません";
            return null;
        }

        var reference = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);
        if (reference is null)
        {
            problem = "FFmpeg が Vulkan に対応していません";
            return null;
        }

        var owner = GCHandle.Alloc(device);
        var context = (AVHWDeviceContext*)reference->data;
        context->user_opaque = (void*)GCHandle.ToIntPtr(owner);
        var hw = (byte*)context->hwctx;
        *(nint*)(hw + GetProcAddr) = getInstanceProcAddr;
        *(nint*)(hw + Instance) = device.Instance.Handle;
        *(nint*)(hw + PhysicalDevice) = device.PhysicalDevice.Handle;
        *(nint*)(hw + Device) = device.Device.Handle;
        Unsafe.CopyBlock(hw + DeviceFeatures, device.EnabledFeatures, (uint)sizeof(VkPhysicalDeviceFeatures2));
        var (instanceNames, instanceCount) = device.InstanceExtensionNames;
        var (deviceNames, deviceCount) = device.DeviceExtensionNames;
        *(nint*)(hw + InstanceExtensions) = instanceCount == 0 ? 0 : instanceNames;
        *(int*)(hw + InstanceExtensionCount) = instanceCount;
        *(nint*)(hw + DeviceExtensions) = deviceCount == 0 ? 0 : deviceNames;
        *(int*)(hw + DeviceExtensionCount) = deviceCount;
        *(nint*)(hw + LockQueueCallback) = (nint)(delegate* unmanaged[Cdecl]<AVHWDeviceContext*, uint, uint, void>)&LockQueue;
        *(nint*)(hw + UnlockQueueCallback) = (nint)(delegate* unmanaged[Cdecl]<AVHWDeviceContext*, uint, uint, void>)&UnlockQueue;

        // キューファミリー：グラフィックス・コンピュート・転送（描画と共有するキュー 1 つ）と、デコード
        var families = (int*)(hw + QueueFamilies);
        families[0] = (int)device.QueueFamily;
        families[1] = 1;
        families[2] = (int)device.QueueFamilyFlags;
        families[3] = 0;
        families[4] = (int)video.Family;
        families[5] = (int)video.QueueCount;
        families[6] = (int)video.FamilyFlags;
        families[7] = (int)video.CodecOperations;
        *(int*)(hw + QueueFamilyCount) = 2;

        var result = ffmpeg.av_hwdevice_ctx_init(reference);
        if (result < 0)
        {
            ffmpeg.av_buffer_unref(&reference);
            owner.Free();
            problem = $"FFmpeg の Vulkan のデバイスを準備できません（エラー {result}）";
            return null;
        }

        problem = null;
        return new FfmpegVulkanDevice(reference, owner);
    }

    public void Dispose()
    {
        if (_reference is null)
        {
            return;
        }

        // このデバイスでデコードしているものに、先に捨てさせる（Vulkan のデバイスを捨てる前に）
        OnClosing();
        var reference = _reference;
        ffmpeg.av_buffer_unref(&reference);
        _reference = null;
        _owner.Free();
    }

    // Vortice が読み込んだのと同じ Vulkan のローダーの vkGetInstanceProcAddr
    private static nint? VulkanLoaderEntry()
    {
        var name = OperatingSystem.IsWindows() ? "vulkan-1.dll" : OperatingSystem.IsMacOS() ? "libvulkan.1.dylib" : "libvulkan.so.1";
        return NativeLibrary.TryLoad(name, out var library) && NativeLibrary.TryGetExport(library, "vkGetInstanceProcAddr", out var entry) ? entry : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LockQueue(AVHWDeviceContext* context, uint family, uint index) => QueueLock(context, family, index)?.Enter();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void UnlockQueue(AVHWDeviceContext* context, uint family, uint index) => QueueLock(context, family, index)?.Exit();

    private static Lock? QueueLock(AVHWDeviceContext* context, uint family, uint index)
    {
        var device = (VulkanDevice)GCHandle.FromIntPtr((nint)context->user_opaque).Target!;
        if (family == device.QueueFamily)
        {
            return device.QueueLock;
        }

        return device.VideoDecode is { } video && family == video.Family && index < device.DecodeQueueLocks.Length ? device.DecodeQueueLocks[index] : null;
    }
}

/// <summary>
/// FFmpeg の Vulkan のフレーム（AVVkFrame）の読み書き。読み書きの間は、FFmpeg の錠（AVVulkanFramesContext.lock_frame）を取る
/// </summary>
internal static unsafe class FfmpegVulkanFrames
{
    // AVVkFrame のオフセット（8 は AV_NUM_DATA_POINTERS）
    private const int Images = 0;
    private const int Access = 208;
    private const int Layouts = 272;
    private const int Semaphores = 304;
    private const int SemaphoreValues = 368;

    // AVVulkanFramesContext のオフセット
    private const int LockFrame = 128;
    private const int UnlockFrame = 136;

    /// <summary>デコードしたフレームの画像（1 つの多平面の画像か、平面ごとの画像）と、そのときの同期の状態</summary>
    public readonly record struct Plane(VkImage Image, VkImageLayout Layout, VkSemaphore Semaphore, ulong Value);

    /// <summary>
    /// フレームの画像と、待つべきセマフォの値を読み、この後の描画の仕事が「その値を待ってから読み、値 + 1 を合図する」ことにする
    /// （画像は TRANSFER_SRC_OPTIMAL にして読む）。FFmpeg は、次にこのフレームを使うとき、値 + 1 を待つ
    /// </summary>
    public static Plane[] Acquire(AVFrame* frame)
    {
        var frames = (AVHWFramesContext*)frame->hw_frames_ctx->data;
        var vulkanFrames = (byte*)frames->hwctx;
        var vkFrame = (byte*)frame->data[0];
        var lockFrame = *(delegate* unmanaged[Cdecl]<AVHWFramesContext*, byte*, void>*)(vulkanFrames + LockFrame);
        var unlockFrame = *(delegate* unmanaged[Cdecl]<AVHWFramesContext*, byte*, void>*)(vulkanFrames + UnlockFrame);
        if (lockFrame is not null)
        {
            lockFrame(frames, vkFrame);
        }

        try
        {
            var planes = new List<Plane>();
            for (var i = 0; i < 8; i++)
            {
                var image = ((ulong*)(vkFrame + Images))[i];
                if (image == 0)
                {
                    break;
                }

                var layout = (VkImageLayout)((int*)(vkFrame + Layouts))[i];
                var semaphore = ((ulong*)(vkFrame + Semaphores))[i];
                var value = ((ulong*)(vkFrame + SemaphoreValues))[i];
                planes.Add(new Plane(new VkImage(image), layout, new VkSemaphore(semaphore), value));

                // 描画の仕事が終わったときの状態にしておく
                ((int*)(vkFrame + Layouts))[i] = (int)VkImageLayout.TransferSrcOptimal;
                ((ulong*)(vkFrame + Access))[i] = (ulong)VkAccessFlags2.TransferRead;
                ((ulong*)(vkFrame + SemaphoreValues))[i] = value + 1;
            }

            return [.. planes];
        }
        finally
        {
            if (unlockFrame is not null)
            {
                unlockFrame(frames, vkFrame);
            }
        }
    }
}
