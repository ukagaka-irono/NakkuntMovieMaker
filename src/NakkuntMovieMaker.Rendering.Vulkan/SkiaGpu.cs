using SkiaSharp;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// 文字・図形を、描画と同じ Vulkan のデバイスとキューで、Skia の GPU 版（GRContext）で描く。
/// 描く先は、こちらが作った RGBA16F の画像（大きさを 64 画素単位にそろえて使い回す）を Skia の面（SKSurface）にしたもの。
/// Skia は描き終えた画像を COLOR_ATTACHMENT_OPTIMAL にしておくので、使う側はそこから写し、元のレイアウトに戻す
/// （SkiaSharp では、Skia が残したレイアウトを読めないので、決まったレイアウトとして扱う。検証レイヤーで確かめている）。
/// GPU のスレッドだけで使う（Skia の GRContext は、同時に複数のスレッドから使えない）。
/// Skia はキューに自分で送るので、送るときは VulkanDevice.QueueLock を取る
/// </summary>
internal sealed class SkiaGpu : IDisposable
{
    private const int Granularity = 64;
    private const long ResourceCacheBytes = 96L << 20;

    private readonly VulkanDevice _device;
    private readonly GpuAllocator _allocator;
    private readonly GRVkBackendContext _backend;
    private readonly GRContext _context;
    private readonly Dictionary<(int Width, int Height, bool Linear), Stack<Target>> _free = [];
    private readonly List<Target> _inUse = [];
    private readonly SKColorSpace _linearSrgb = SKColorSpace.CreateSrgbLinear();

    private SkiaGpu(VulkanDevice device, GpuAllocator allocator, GRVkBackendContext backend, GRContext context)
    {
        _device = device;
        _allocator = allocator;
        _backend = backend;
        _context = context;
    }

    /// <summary>Skia の GPU 版を使うか（macOS は使わない。環境変数 NMM_SKIA_GPU=0 でも使わない）</summary>
    public static bool Wanted => !OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("NMM_SKIA_GPU") != "0";

    /// <summary>作る。作れなければ null と理由</summary>
    public static SkiaGpu? TryCreate(VulkanDevice device, GpuAllocator allocator, out string? problem)
    {
        GRVkBackendContext? backend = null;
        try
        {
            GRVkGetProcedureAddressDelegate getProc = (name, instance, handle) => device.GetProcAddress(name, instance, handle);
            backend = new GRVkBackendContext
            {
                VkInstance = device.Instance.Handle,
                VkPhysicalDevice = device.PhysicalDevice.Handle,
                VkDevice = device.Device.Handle,
                VkQueue = device.Queue.Handle,
                GraphicsQueueIndex = device.QueueFamily,
                MaxAPIVersion = VkVersion.Version_1_3.Value,
                VkPhysicalDeviceFeatures2 = (nint)device.EnabledFeaturesPointer,
                GetProcedureAddress = getProc,
                Extensions = GRVkExtensions.Create(
                    getProc, device.Instance.Handle, device.PhysicalDevice.Handle, [.. device.InstanceExtensionList], [.. device.DeviceExtensionList]),
            };

            GRContext? context;
            lock (device.QueueLock)
            {
                context = GRContext.CreateVulkan(backend);
            }

            if (context is null)
            {
                backend.Dispose();
                problem = "Skia の GPU 版を準備できません";
                return null;
            }

            context.SetResourceCacheLimit(ResourceCacheBytes);
            problem = null;
            return new SkiaGpu(device, allocator, backend, context);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or VulkanException)
        {
            backend?.Dispose();
            problem = $"Skia の GPU 版を準備できません（{ex.Message}）";
            return null;
        }
    }

    /// <summary>
    /// width × height の透明な画像に、draw で描く（Skia に記録させるだけで、まだ送らない。送るのは EndFrame）。
    /// 返す画像は、このフレームの間はほかに使わない。画像は COLOR_ATTACHMENT_OPTIMAL で、(0, 0) から width × height に描いてある
    /// </summary>
    public GpuImage Draw(int width, int height, bool linear, Action<SKCanvas> draw)
    {
        var target = Take(width, height, linear);
        _inUse.Add(target);
        var canvas = target.Surface.Canvas;
        canvas.Save();
        canvas.ResetMatrix();
        canvas.Clear(SKColors.Transparent);
        canvas.ClipRect(new SKRect(0, 0, width, height));
        draw(canvas);
        canvas.Restore();
        target.Surface.Flush(false, false);
        return target.Image;
    }

    /// <summary>
    /// このフレームで記録させた描画を、キューに送る。描画の仕事（GpuScheduler）より先に送るので、同じキューの上で Skia の描画が先になる。
    /// 使った画像は、次のフレームから使い回す（次のフレームの Skia の描画は、この仕事の写しの後に送られる）
    /// </summary>
    public void EndFrame()
    {
        if (_inUse.Count == 0)
        {
            return;
        }

        lock (_device.QueueLock)
        {
            _context.Submit(false);
        }

        foreach (var target in _inUse)
        {
            Pool(target.Key).Push(target);
        }

        _inUse.Clear();
    }

    private Target Take(int width, int height, bool linear)
    {
        var key = (Round(width), Round(height), linear);
        if (Pool(key).TryPop(out var target))
        {
            return target;
        }

        var image = _allocator.CreateImage(
            key.Item1, key.Item2, VkFormat.R16G16B16A16Sfloat,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst);
        try
        {
            var info = new GRVkImageInfo
            {
                Image = image.Image.Handle,
                ImageTiling = (uint)VkImageTiling.Optimal,
                ImageLayout = (uint)VkImageLayout.Undefined,
                Format = (uint)VkFormat.R16G16B16A16Sfloat,
                ImageUsageFlags = (uint)image.Usage,
                SampleCount = 1,
                LevelCount = 1,
                CurrentQueueFamily = _device.QueueFamily,
                SharingMode = (uint)VkSharingMode.Exclusive,
            };
            var texture = new GRBackendTexture(key.Item1, key.Item2, info);
            var surface = SKSurface.Create(_context, texture, GRSurfaceOrigin.TopLeft, 1, SKColorType.RgbaF16, linear ? _linearSrgb : null);
            if (surface is null)
            {
                texture.Dispose();
                throw new VulkanException("Skia の GPU 版の描画先を作れません");
            }

            image.Layout = VkImageLayout.ColorAttachmentOptimal;
            return new Target(key, image, texture, surface);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private Stack<Target> Pool((int, int, bool) key)
    {
        if (!_free.TryGetValue(key, out var pool))
        {
            pool = new Stack<Target>();
            _free[key] = pool;
        }

        return pool;
    }

    private static int Round(int size) => Math.Max(Granularity, (size + Granularity - 1) / Granularity * Granularity);

    /// <summary>GPU が使い終わってから呼ぶ（Skia も、捨てるときにキューを待つので、送るときの錠を取る）</summary>
    public void Dispose()
    {
        lock (_device.QueueLock)
        {
            foreach (var target in _free.Values.SelectMany(pool => pool).Concat(_inUse))
            {
                target.Surface.Dispose();
                target.Texture.Dispose();
            }

            _context.Dispose();
        }

        foreach (var target in _free.Values.SelectMany(pool => pool).Concat(_inUse))
        {
            target.Image.Dispose();
        }

        _free.Clear();
        _inUse.Clear();
        _backend.Dispose();
        _linearSrgb.Dispose();
    }

    private sealed record Target((int Width, int Height, bool Linear) Key, GpuImage Image, GRBackendTexture Texture, SKSurface Surface);
}
