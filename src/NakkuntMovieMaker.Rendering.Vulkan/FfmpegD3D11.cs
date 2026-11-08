using System.Runtime.Versioning;
using NakkuntMovieMaker.Media;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// Windows で、動画を d3d11va（描画と同じ GPU）でデコードし、デコードした画像をメインメモリに写さずに描画に渡すためのデバイス。
/// FFmpeg がデコードした 1 枚を、D3D11 の中で共有のテクスチャ（D3D11SharedTexture）に写し、それを Vulkan の画像として取り込む
/// （VK_KHR_external_memory_win32。取り込むのはテクスチャごとに 1 回で、使い回す）。
/// H.264 は、ソフトウェアでデコードして YUV の平面で送るほうが速いので、既定では使わない（環境変数 NMM_D3D11_H264=1 で使う）。
/// 環境変数 NMM_D3D11_IMPORT=0 で、使わないようにできる
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class FfmpegD3D11Device : HardwareVideoDevice, IDisposable
{
    private readonly VulkanDevice _device;
    private readonly D3D11VideoDevice _video;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ImportedImage> _images = [];
    private bool _disposed;

    private FfmpegD3D11Device(VulkanDevice device, D3D11VideoDevice video)
    {
        _device = device;
        _video = video;
    }

    public override nint Reference => _video.Reference;

    /// <summary>デバイスを作る。使えなければ null と理由</summary>
    public static FfmpegD3D11Device? TryCreate(VulkanDevice device, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (Environment.GetEnvironmentVariable("NMM_D3D11_IMPORT") == "0")
        {
            problem = "環境変数 NMM_D3D11_IMPORT=0 で、D3D11 からの取り込みを使わないようにしています";
            return null;
        }

        if (!device.ExternalMemoryWin32 || !CanImport(device, VkFormat.G8B8R82Plane420Unorm))
        {
            problem = "GPU が、D3D11 の画像の取り込みに対応していません";
            return null;
        }

        var video = D3D11VideoDevice.TryCreate(device.Info.Luid, out problem);
        return video is null ? null : new FfmpegD3D11Device(device, video);
    }

    public override VideoDecoderOptions DecoderOptions(VideoDecoderOptions options) => options with { D3D11 = _video };

    public override bool Accepts(string? codec) =>
        codec is not null && (codec != "h264" || Environment.GetEnvironmentVariable("NMM_D3D11_H264") == "1");

    /// <summary>共有のテクスチャを取り込んだ Vulkan の画像（2 つの平面を持つ YUV の画像。初めてなら取り込む）</summary>
    internal VkImage Import(D3D11SharedTexture texture)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_images.TryGetValue(texture.Id, out var imported))
            {
                imported = CreateImage(texture);
                _images[texture.Id] = imported;
            }

            return imported.Image;
        }
    }

    private static VkFormat FormatOf(VideoPlaneFormat format) =>
        format == VideoPlaneFormat.P010 ? VkFormat.G10X6B10X6R10X62Plane420Unorm3Pack16 : VkFormat.G8B8R82Plane420Unorm;

    // この形式の画像を、D3D11 のテクスチャから取り込めるか
    private static bool CanImport(VulkanDevice device, VkFormat format)
    {
        var external = new VkPhysicalDeviceExternalImageFormatInfo { handleType = VkExternalMemoryHandleTypeFlags.D3D11Texture };
        var info = new VkPhysicalDeviceImageFormatInfo2
        {
            pNext = &external,
            format = format,
            type = VkImageType.Image2D,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferSrc,
        };
        var externalProperties = new VkExternalImageFormatProperties();
        var properties = new VkImageFormatProperties2 { pNext = &externalProperties };
        var result = device.InstanceApi.vkGetPhysicalDeviceImageFormatProperties2(device.PhysicalDevice, &info, &properties);
        return result == VkResult.Success
            && (externalProperties.externalMemoryProperties.externalMemoryFeatures & VkExternalMemoryFeatureFlags.Importable) != 0;
    }

    private ImportedImage CreateImage(D3D11SharedTexture texture)
    {
        var api = _device.DeviceApi;
        var external = new VkExternalMemoryImageCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.D3D11Texture };
        var info = new VkImageCreateInfo
        {
            pNext = &external,
            imageType = VkImageType.Image2D,
            format = FormatOf(texture.Format),
            extent = new VkExtent3D(texture.Width, texture.Height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        VkImage image;
        Check(api.vkCreateImage(&info, null, &image), "画像を作れません");

        try
        {
            VkMemoryRequirements requirements;
            api.vkGetImageMemoryRequirements(image, &requirements);
            var typeBits = requirements.memoryTypeBits;
            var handleProperties = new VkMemoryWin32HandlePropertiesKHR();
            if (api.vkGetMemoryWin32HandlePropertiesKHR(VkExternalMemoryHandleTypeFlags.D3D11Texture, texture.Handle, &handleProperties) == VkResult.Success
                && (typeBits & handleProperties.memoryTypeBits) != 0)
            {
                typeBits &= handleProperties.memoryTypeBits;
            }

            var dedicated = new VkMemoryDedicatedAllocateInfo { image = image };
            var import = new VkImportMemoryWin32HandleInfoKHR
            {
                pNext = &dedicated,
                handleType = VkExternalMemoryHandleTypeFlags.D3D11Texture,
                handle = texture.Handle,
            };
            var allocate = new VkMemoryAllocateInfo
            {
                pNext = &import,
                allocationSize = requirements.size,
                memoryTypeIndex = MemoryType(typeBits),
            };
            VkDeviceMemory memory;
            Check(api.vkAllocateMemory(&allocate, null, &memory), "メモリを取り込めません");
            var bound = api.vkBindImageMemory(image, memory, 0);
            if (bound != VkResult.Success)
            {
                api.vkFreeMemory(memory, null);
                Check(bound, "メモリを画像に結び付けられません");
            }

            return new ImportedImage(image, memory);
        }
        catch
        {
            api.vkDestroyImage(image, null);
            throw;
        }
    }

    // 使えるメモリの種類のうち、GPU のメモリ（なければ最初のもの）
    private uint MemoryType(uint typeBits)
    {
        VkPhysicalDeviceMemoryProperties properties;
        _device.InstanceApi.vkGetPhysicalDeviceMemoryProperties(_device.PhysicalDevice, &properties);
        uint? first = null;
        for (var i = 0u; i < properties.memoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
            {
                continue;
            }

            first ??= i;
            if ((properties.memoryTypes[(int)i].propertyFlags & VkMemoryPropertyFlags.DeviceLocal) != 0)
            {
                return i;
            }
        }

        return first ?? throw new VulkanException("D3D11 の画像を取り込めるメモリがありません");
    }

    private static void Check(VkResult result, string what)
    {
        if (result != VkResult.Success)
        {
            throw new VulkanException($"D3D11 の画像を取り込めません：{what}（{result}）");
        }
    }

    /// <summary>このデバイスでデコードしているデコーダーを先に捨てさせてから、取り込んだ画像とデバイスを捨てる（描画の GPU が使い終わってから呼ぶ）</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        OnClosing();
        var api = _device.DeviceApi;
        foreach (var imported in _images.Values)
        {
            api.vkDestroyImage(imported.Image, null);
            api.vkFreeMemory(imported.Memory, null);
        }

        _images.Clear();
        _video.Dispose();
    }

    private readonly record struct ImportedImage(VkImage Image, VkDeviceMemory Memory);
}
