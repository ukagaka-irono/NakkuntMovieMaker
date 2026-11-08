using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>GPU の種類</summary>
public enum VulkanDeviceType
{
    Other,
    Integrated,
    Discrete,
    Virtual,

    /// <summary>CPU で動く Vulkan（lavapipe・SwiftShader など）。テスト用で、普段は選ばない</summary>
    Software,
}

/// <summary>GPU の情報。Luid は Windows のアダプターの識別子（画面との連携で、同じ GPU かを確かめるのに使う）</summary>
public sealed record VulkanDeviceInfo(string Name, VulkanDeviceType Type, Version ApiVersion, string Driver, byte[]? Luid, bool Suitable, string? Unsuitable);

/// <summary>
/// デバイスの選び方。DeviceName を指定すれば、その名前の GPU を選ぶ。AllowSoftware なら CPU で動く Vulkan も選べる
/// （CI のテスト用。環境変数 NMM_VULKAN_ALLOW_SOFTWARE=1 でも同じ）。Validation なら検証レイヤーを使う（NMM_VULKAN_VALIDATION=1 でも同じ）。
/// VideoDecode なら、Vulkan Video のデコード用のキューと拡張も用意する（NMM_VULKAN_VIDEO=1 でも同じ）。
/// FFmpeg 9.0.2 と基準の PC の Intel のドライバーでは、H.264 の動画によって、Vulkan Video でデコードするとプロセスごと落ちる
/// （FFmpeg の ffmpeg コマンドでも同じ）ので、今は試験的な機能として、既定では使わない
/// </summary>
public sealed record VulkanDeviceOptions(string? DeviceName = null, bool AllowSoftware = false, bool Validation = false, bool VideoDecode = false)
{
    public static VulkanDeviceOptions Default { get; } = new();

    internal bool SoftwareAllowed => AllowSoftware || Environment.GetEnvironmentVariable("NMM_VULKAN_ALLOW_SOFTWARE") == "1";

    internal bool VideoDecodeEnabled => VideoDecode || Environment.GetEnvironmentVariable("NMM_VULKAN_VIDEO") == "1";

    internal bool ValidationEnabled => Validation || Environment.GetEnvironmentVariable("NMM_VULKAN_VALIDATION") == "1";
}

/// <summary>
/// Vulkan Video のデコード（FFmpeg に同じデバイスでデコードさせ、フレームをコピーせずに受け取るため）。
/// Family はデコード用のキューファミリー、QueueCount はその中で作ったキューの数、CodecOperations は対応しているコーデック（VkVideoCodecOperationFlagsKHR）
/// </summary>
public sealed record VulkanVideoDecode(uint Family, uint QueueCount, uint CodecOperations, VkQueueFlags FamilyFlags);

/// <summary>Vulkan を使えなかった。Message は利用者に表示できる文</summary>
public sealed class VulkanException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Vulkan のインスタンス・GPU・デバイス・キュー。グラフィックス・コンピュートのキューは 1 つで、Skia と自前のシェーダーで共有する
/// （キューへの送信は、GpuScheduler の 1 本のスレッドだけが行う）
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    internal const string ExternalMemoryWin32Extension = "VK_KHR_external_memory_win32";

    // Vulkan 1.2 以上と、タイムラインセマフォ・RGBA16F の読み書きが必要
    private static readonly VkVersion MinimumApi = VkVersion.Version_1_2;
    private static readonly ConcurrentQueue<string> Messages = new();
    private static readonly Lock LoaderGate = new();
    private static VkResult? _loaded;

    private readonly VkDebugUtilsMessengerEXT _messenger;
    private readonly DeviceSetup _setup;
    private bool _disposed;

    private VulkanDevice(VkInstance instance, VkInstanceApi instanceApi, VkPhysicalDevice physical, VulkanDeviceInfo info, VkDevice device, VkDeviceApi deviceApi,
        uint queueFamily, VkQueue queue, VkDebugUtilsMessengerEXT messenger, DeviceSetup setup)
    {
        _setup = setup;
        VideoDecode = setup.Video;
        QueueFamilyFlags = setup.GraphicsFlags;
        DecodeQueueLocks = [.. Enumerable.Range(0, (int)(setup.Video?.QueueCount ?? 0)).Select(_ => new Lock())];
        Instance = instance;
        InstanceApi = instanceApi;
        PhysicalDevice = physical;
        Info = info;
        Device = device;
        DeviceApi = deviceApi;
        QueueFamily = queueFamily;
        Queue = queue;
        _messenger = messenger;
    }

    public VkInstance Instance { get; }

    public VkInstanceApi InstanceApi { get; }

    public VkPhysicalDevice PhysicalDevice { get; }

    public VulkanDeviceInfo Info { get; }

    public VkDevice Device { get; }

    public VkDeviceApi DeviceApi { get; }

    /// <summary>グラフィックス・コンピュート・転送のキューファミリー</summary>
    public uint QueueFamily { get; }

    public VkQueue Queue { get; }

    /// <summary>グラフィックス・コンピュートのキューファミリーでできること</summary>
    public VkQueueFlags QueueFamilyFlags { get; }

    /// <summary>
    /// グラフィックス・コンピュートのキューに送るときに取る錠（FFmpeg も同じキューに送るので、送る前に取る）
    /// </summary>
    public Lock QueueLock { get; } = new();

    /// <summary>デコード用のキューの錠（FFmpeg だけが使う）</summary>
    internal Lock[] DecodeQueueLocks { get; }

    /// <summary>Vulkan Video のデコードに使えるキューと、有効にした拡張（使えなければ null）</summary>
    public VulkanVideoDecode? VideoDecode { get; }

    /// <summary>D3D11 の共有のテクスチャを取り込める（VK_KHR_external_memory_win32 を有効にした）</summary>
    public bool ExternalMemoryWin32 => _setup.DeviceExtensions.Values.Contains(ExternalMemoryWin32Extension);

    /// <summary>有効にしたインスタンスの拡張（UTF-8 の文字列の配列。FFmpeg に渡す）</summary>
    internal (nint Names, int Count) InstanceExtensionNames => (_setup.InstanceExtensions.Pointer, _setup.InstanceExtensions.Count);

    /// <summary>有効にしたデバイスの拡張（UTF-8 の文字列の配列。FFmpeg に渡す）</summary>
    internal (nint Names, int Count) DeviceExtensionNames => (_setup.DeviceExtensions.Pointer, _setup.DeviceExtensions.Count);

    /// <summary>有効にした機能（VkPhysicalDeviceFeatures2 と、つないだ 1.1・1.2・1.3 の機能。デバイスがある間は有効。FFmpeg に渡す）</summary>
    internal VkPhysicalDeviceFeatures2* EnabledFeatures => _setup.Features;

    /// <summary>検証レイヤーが出した警告とエラー（検証レイヤーを使ったときだけ。テストで 0 件を確かめる）</summary>
    public static IReadOnlyCollection<string> ValidationMessages => Messages;

    public static void ClearValidationMessages() => Messages.Clear();

    /// <summary>
    /// Vulkan を使う準備をして、デバイスを作る
    /// </summary>
    /// <exception cref="VulkanException">Vulkan がない・使える GPU がない・無効にされている</exception>
    public static VulkanDevice Create(VulkanDeviceOptions? options = null)
    {
        options ??= VulkanDeviceOptions.Default;
        if (Environment.GetEnvironmentVariable("NMM_VULKAN_DISABLE") == "1")
        {
            throw new VulkanException("GPU での描画は、無効にされています（NMM_VULKAN_DISABLE=1）");
        }

        EnsureLoader();
        var (instance, api, messenger, instanceExtensions) = CreateInstance(options);
        try
        {
            var candidates = Enumerate(instance, api);
            var chosen = Choose(candidates, options)
                ?? throw new VulkanException(candidates.Count == 0
                    ? "Vulkan に対応した GPU が見つかりません"
                    : $"使える GPU がありません（{string.Join("、", candidates.Select(c => $"{c.Info.Name}: {c.Info.Unsuitable}"))}）");
            return CreateDevice(instance, api, messenger, chosen.Physical, chosen.Info, chosen.QueueFamily, instanceExtensions, options.VideoDecodeEnabled);
        }
        catch
        {
            DestroyInstance(instance, api, messenger);
            throw;
        }
    }

    /// <summary>Vulkan に対応した GPU の一覧（使えるかどうかと、その理由つき）。Vulkan がなければ空</summary>
    public static IReadOnlyList<VulkanDeviceInfo> ListDevices()
    {
        try
        {
            EnsureLoader();
            var (instance, api, messenger, _) = CreateInstance(VulkanDeviceOptions.Default with { Validation = false });
            try
            {
                return [.. Enumerate(instance, api).Select(candidate => candidate.Info)];
            }
            finally
            {
                DestroyInstance(instance, api, messenger);
            }
        }
        catch (VulkanException)
        {
            return [];
        }
    }

    /// <summary>関数のアドレス（Skia に渡す）。device が 0 ならインスタンスの関数</summary>
    public nint GetProcAddress(string name, nint instance, nint device) =>
        device != 0
            ? (nint)InstanceApi.vkGetDeviceProcAddr(new VkDevice(device), name).Value
            : (nint)vkGetInstanceProcAddr(new VkInstance(instance == 0 ? Instance.Handle : instance), name).Value;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeviceApi.vkDeviceWaitIdle();
        DeviceApi.vkDestroyDevice(null);
        DestroyInstance(Instance, InstanceApi, _messenger);
        _setup.Dispose();
    }

    // Vulkan のローダー（Windows・Linux は OS・ドライバーのもの、macOS は同梱の MoltenVK）を読み込む
    private static void EnsureLoader()
    {
        lock (LoaderGate)
        {
            if (_loaded is null)
            {
                try
                {
                    _loaded = OperatingSystem.IsMacOS()
                        ? vkInitialize(Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"))
                        : vkInitialize();
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    _loaded = VkResult.ErrorInitializationFailed;
                }
            }

            if (_loaded != VkResult.Success)
            {
                throw new VulkanException("Vulkan を読み込めません（GPU のドライバーが Vulkan に対応していないか、入っていません）");
            }
        }
    }

    private static (VkInstance Instance, VkInstanceApi Api, VkDebugUtilsMessengerEXT Messenger, List<string> Extensions) CreateInstance(VulkanDeviceOptions options)
    {
        var available = InstanceExtensions();
        var extensions = new List<string>();
        var layers = new List<string>();
        var flags = VkInstanceCreateFlags.None;
        if (OperatingSystem.IsMacOS() && available.Contains("VK_KHR_portability_enumeration"))
        {
            // MoltenVK は「完全には準拠していない実装」なので、明示して列挙させる
            extensions.Add("VK_KHR_portability_enumeration");
            flags |= VkInstanceCreateFlags.EnumeratePortabilityKHR;
        }

        var validation = options.ValidationEnabled && InstanceLayers().Contains("VK_LAYER_KHRONOS_validation") && available.Contains("VK_EXT_debug_utils");
        if (validation)
        {
            layers.Add("VK_LAYER_KHRONOS_validation");
            extensions.Add("VK_EXT_debug_utils");
        }

        var name = new VkUtf8ReadOnlyString("NakkuntMovieMaker"u8);
        using var extensionNames = new VkStringArray(extensions);
        using var layerNames = new VkStringArray(layers);
        var app = new VkApplicationInfo { pApplicationName = name, pEngineName = name, apiVersion = VkVersion.Version_1_3 };
        var createInfo = new VkInstanceCreateInfo
        {
            flags = flags,
            pApplicationInfo = &app,
            enabledExtensionCount = extensionNames.Length,
            ppEnabledExtensionNames = extensionNames,
            enabledLayerCount = layerNames.Length,
            ppEnabledLayerNames = layerNames,
        };
        VkInstance instance;
        var result = vkCreateInstance(&createInfo, null, &instance);
        if (result != VkResult.Success)
        {
            throw new VulkanException($"Vulkan を使い始められません（{result}）");
        }

        var api = GetApi(instance);
        var messenger = VkDebugUtilsMessengerEXT.Null;
        if (validation)
        {
            var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
            {
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation | VkDebugUtilsMessageTypeFlagsEXT.Performance,
                pfnUserCallback = &OnMessage,
            };
            api.vkCreateDebugUtilsMessengerEXT(&messengerInfo, null, &messenger);
        }

        return (instance, api, messenger, extensions);
    }

    [UnmanagedCallersOnly]
    private static uint OnMessage(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        var text = Marshal.PtrToStringUTF8((nint)data->pMessage) ?? "";
        Messages.Enqueue($"{severity}: {text}");
        return 0; // VK_FALSE：呼び出しを止めない
    }

    private static void DestroyInstance(VkInstance instance, VkInstanceApi api, VkDebugUtilsMessengerEXT messenger)
    {
        if (messenger != VkDebugUtilsMessengerEXT.Null)
        {
            api.vkDestroyDebugUtilsMessengerEXT(messenger, null);
        }

        api.vkDestroyInstance(null);
    }

    private sealed record Candidate(VkPhysicalDevice Physical, VulkanDeviceInfo Info, uint QueueFamily);

    private static List<Candidate> Enumerate(VkInstance instance, VkInstanceApi api)
    {
        uint count = 0;
        api.vkEnumeratePhysicalDevices(&count, null);
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* pointer = devices)
        {
            api.vkEnumeratePhysicalDevices(&count, pointer);
        }

        return [.. devices.Select(device => Describe(api, device))];
    }

    private static Candidate Describe(VkInstanceApi api, VkPhysicalDevice device)
    {
        var id = new VkPhysicalDeviceIDProperties();
        var properties2 = new VkPhysicalDeviceProperties2 { pNext = &id };
        api.vkGetPhysicalDeviceProperties2(device, &properties2);
        var properties = properties2.properties;
        var name = Marshal.PtrToStringUTF8((nint)properties.deviceName) ?? "（名前なし）";
        var type = properties.deviceType switch
        {
            VkPhysicalDeviceType.IntegratedGpu => VulkanDeviceType.Integrated,
            VkPhysicalDeviceType.DiscreteGpu => VulkanDeviceType.Discrete,
            VkPhysicalDeviceType.VirtualGpu => VulkanDeviceType.Virtual,
            VkPhysicalDeviceType.Cpu => VulkanDeviceType.Software,
            _ => VulkanDeviceType.Other,
        };
        var version = new VkVersion(properties.apiVersion);
        var apiVersion = new Version((int)version.Major, (int)version.Minor, (int)version.Patch);
        byte[]? luid = id.deviceLUIDValid ? new ReadOnlySpan<byte>(id.deviceLUID, 8).ToArray() : null;

        // 条件：Vulkan 1.2 以上、グラフィックスとコンピュートのキュー、タイムラインセマフォ、RGBA16F の読み書きとサンプリング
        var family = GraphicsComputeFamily(api, device);
        var timeline = new VkPhysicalDeviceVulkan12Features();
        var features2 = new VkPhysicalDeviceFeatures2 { pNext = &timeline };
        api.vkGetPhysicalDeviceFeatures2(device, &features2);
        VkFormatProperties format;
        api.vkGetPhysicalDeviceFormatProperties(device, VkFormat.R16G16B16A16Sfloat, &format);
        var needed = VkFormatFeatureFlags.StorageImage | VkFormatFeatureFlags.SampledImage | VkFormatFeatureFlags.SampledImageFilterLinear;
        string? unsuitable = properties.apiVersion < MinimumApi.Value ? $"Vulkan {apiVersion} は古すぎます（1.2 以上が必要）"
            : family is null ? "グラフィックスとコンピュートのキューがありません"
            : !timeline.timelineSemaphore ? "タイムラインセマフォに対応していません"
            : (format.optimalTilingFeatures & needed) != needed ? "16 ビット浮動小数点の画像（RGBA16F）を読み書きできません"
            : null;
        var driver = DriverVersion(properties.vendorID, properties.driverVersion);
        var info = new VulkanDeviceInfo(name, type, apiVersion, driver, luid, unsuitable is null, unsuitable);
        return new Candidate(device, info, family ?? 0);
    }

    // ドライバーの版の数え方は、作っている会社ごとに違う（Intel の Windows 版は 101.7080 のような形）
    private static string DriverVersion(uint vendor, uint version) => vendor switch
    {
        0x10DE => $"{(version >> 22) & 0x3FF}.{(version >> 14) & 0xFF}",
        0x8086 when OperatingSystem.IsWindows() => $"{version >> 14}.{version & 0x3FFF}",
        _ => new VkVersion(version) is var v ? $"{v.Major}.{v.Minor}.{v.Patch}" : "",
    };

    private static uint? GraphicsComputeFamily(VkInstanceApi api, VkPhysicalDevice device)
    {
        uint count = 0;
        api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* pointer = families)
        {
            api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);
        }

        const VkQueueFlags Needed = VkQueueFlags.Graphics | VkQueueFlags.Compute;
        var index = Array.FindIndex(families, family => (family.queueFlags & Needed) == Needed);
        return index < 0 ? null : (uint)index;
    }

    // 名前を指定されていればその GPU、なければ 単体の GPU → 内蔵の GPU → 仮想の GPU の順（CPU で動く Vulkan は許されたときだけ）
    private static Candidate? Choose(List<Candidate> candidates, VulkanDeviceOptions options)
    {
        var usable = candidates.Where(c => c.Info.Suitable && (c.Info.Type != VulkanDeviceType.Software || options.SoftwareAllowed)).ToList();
        if (options.DeviceName is { Length: > 0 } wanted)
        {
            return usable.FirstOrDefault(c => c.Info.Name == wanted)
                ?? throw new VulkanException($"指定した GPU（{wanted}）が見つからないか、使えません");
        }

        return usable.OrderByDescending(c => c.Info.Type switch
        {
            VulkanDeviceType.Discrete => 4,
            VulkanDeviceType.Integrated => 3,
            VulkanDeviceType.Virtual => 2,
            VulkanDeviceType.Other => 1,
            _ => 0,
        }).FirstOrDefault();
    }

    private static VulkanDevice CreateDevice(
        VkInstance instance, VkInstanceApi api, VkDebugUtilsMessengerEXT messenger, VkPhysicalDevice physical, VulkanDeviceInfo info, uint family, List<string> instanceExtensions,
        bool wantVideo)
    {
        var available = DeviceExtensions(api, physical);
        var extensions = new List<string>();
        if (available.Contains("VK_KHR_portability_subset"))
        {
            extensions.Add("VK_KHR_portability_subset");
        }

        // Windows では、d3d11va でデコードした画像（共有のテクスチャ）を取り込めるようにする
        if (OperatingSystem.IsWindows() && available.Contains(ExternalMemoryWin32Extension))
        {
            extensions.Add(ExternalMemoryWin32Extension);
        }

        var families = QueueFamilies(api, physical);
        var video = wantVideo ? FindVideoDecode(api, physical, available, families, family, info.ApiVersion) : null;
        if (video is not null)
        {
            extensions.AddRange(video.Value.Extensions);
        }

        // 有効にする機能（FFmpeg にも渡すので、デバイスがある間は残しておく）
        var setup = new DeviceSetup(instanceExtensions, extensions, info.ApiVersion >= new Version(1, 3), video?.Decode, families[family].queueFlags);
        try
        {
            setup.EnableSupported(api, physical, wantVideo: video is not null);

            var priorities = stackalloc float[Math.Max(1, (int)(video?.Decode.QueueCount ?? 1))];
            for (var i = 0; i < Math.Max(1, (int)(video?.Decode.QueueCount ?? 1)); i++)
            {
                priorities[i] = 1f;
            }

            var queueInfos = stackalloc VkDeviceQueueCreateInfo[2];
            queueInfos[0] = new VkDeviceQueueCreateInfo { queueFamilyIndex = family, queueCount = 1, pQueuePriorities = priorities };
            var queueCount = 1u;
            if (video is { } decode)
            {
                queueInfos[1] = new VkDeviceQueueCreateInfo { queueFamilyIndex = decode.Decode.Family, queueCount = decode.Decode.QueueCount, pQueuePriorities = priorities };
                queueCount = 2;
            }

            using var extensionNames = new VkStringArray(extensions);
            var createInfo = new VkDeviceCreateInfo
            {
                pNext = setup.Features,
                queueCreateInfoCount = queueCount,
                pQueueCreateInfos = queueInfos,
                enabledExtensionCount = extensionNames.Length,
                ppEnabledExtensionNames = extensionNames,
            };
            VkDevice device;
            var result = api.vkCreateDevice(physical, &createInfo, null, &device);
            if (result != VkResult.Success)
            {
                throw new VulkanException($"GPU（{info.Name}）を使い始められません（{result}）");
            }

            var deviceApi = GetApi(instance, device);
            VkQueue queue;
            deviceApi.vkGetDeviceQueue(family, 0, &queue);
            return new VulkanDevice(instance, api, physical, info, device, deviceApi, family, queue, messenger, setup);
        }
        catch
        {
            setup.Dispose();
            throw;
        }
    }

    private static VkQueueFamilyProperties[] QueueFamilies(VkInstanceApi api, VkPhysicalDevice device)
    {
        uint count = 0;
        api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* pointer = families)
        {
            api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);
        }

        return families;
    }

    // Vulkan Video のデコード：デコード用のキューファミリーと、対応しているコーデックの拡張。
    // Vulkan 1.3 以上（FFmpeg が synchronization2 などを使う）のときだけ。macOS（MoltenVK）では使わない
    private static (VulkanVideoDecode Decode, List<string> Extensions)? FindVideoDecode(
        VkInstanceApi api, VkPhysicalDevice physical, HashSet<string> available, VkQueueFamilyProperties[] families, uint graphicsFamily, Version apiVersion)
    {
        if (OperatingSystem.IsMacOS() || apiVersion < new Version(1, 3)
            || !available.Contains("VK_KHR_video_queue") || !available.Contains("VK_KHR_video_decode_queue"))
        {
            return null;
        }

        (string Name, uint Operation)[] codecs =
        [
            ("VK_KHR_video_decode_h264", 0x1), ("VK_KHR_video_decode_h265", 0x2), ("VK_KHR_video_decode_av1", 0x4), ("VK_KHR_video_decode_vp9", 0x8),
        ];
        var enabledCodecs = codecs.Where(codec => available.Contains(codec.Name)).ToList();
        var operations = enabledCodecs.Aggregate(0u, (all, codec) => all | codec.Operation);
        if (operations == 0)
        {
            return null;
        }

        // キューファミリーごとの、対応しているコーデック
        var count = (uint)families.Length;
        var videoProperties = new VkQueueFamilyVideoPropertiesKHR[count];
        var properties = new VkQueueFamilyProperties2[count];
        fixed (VkQueueFamilyVideoPropertiesKHR* video = videoProperties)
        fixed (VkQueueFamilyProperties2* pointer = properties)
        {
            for (var i = 0; i < count; i++)
            {
                video[i] = new VkQueueFamilyVideoPropertiesKHR();
                pointer[i] = new VkQueueFamilyProperties2 { pNext = &video[i] };
            }

            api.vkGetPhysicalDeviceQueueFamilyProperties2(physical, &count, pointer);
        }

        for (var i = 0u; i < count; i++)
        {
            var flags = properties[i].queueFamilyProperties.queueFlags;
            var supported = (uint)videoProperties[i].videoCodecOperations & operations;
            if (i != graphicsFamily && (flags & VkQueueFlags.VideoDecodeKHR) != 0 && supported != 0)
            {
                var extensions = new List<string> { "VK_KHR_video_queue", "VK_KHR_video_decode_queue" };
                extensions.AddRange(enabledCodecs.Where(codec => (codec.Operation & supported) != 0).Select(codec => codec.Name));
                foreach (var optional in new[] { "VK_KHR_video_maintenance1" })
                {
                    if (available.Contains(optional))
                    {
                        extensions.Add(optional);
                    }
                }

                return (new VulkanVideoDecode(i, properties[i].queueFamilyProperties.queueCount, supported, flags), extensions);
            }
        }

        return null;
    }

    /// <summary>
    /// デバイスを作るときに有効にした拡張と機能。FFmpeg にも同じものを渡すので、デバイスを捨てるまで残す
    /// </summary>
    private sealed class DeviceSetup : IDisposable
    {
        private readonly byte* _block;

        public DeviceSetup(List<string> instanceExtensions, List<string> deviceExtensions, bool vulkan13, VulkanVideoDecode? video, VkQueueFlags graphicsFlags)
        {
            InstanceExtensions = new Utf8Strings(instanceExtensions);
            DeviceExtensions = new Utf8Strings(deviceExtensions);
            Video = video;
            GraphicsFlags = graphicsFlags;

            // VkPhysicalDeviceFeatures2 → 1.1 → 1.2 →（1.3）の順につなぐ
            _block = (byte*)NativeMemory.AllocZeroed((nuint)(sizeof(VkPhysicalDeviceFeatures2) + sizeof(VkPhysicalDeviceVulkan11Features)
                + sizeof(VkPhysicalDeviceVulkan12Features) + sizeof(VkPhysicalDeviceVulkan13Features)));
            Features = (VkPhysicalDeviceFeatures2*)_block;
            Vulkan11 = (VkPhysicalDeviceVulkan11Features*)(_block + sizeof(VkPhysicalDeviceFeatures2));
            Vulkan12 = (VkPhysicalDeviceVulkan12Features*)((byte*)Vulkan11 + sizeof(VkPhysicalDeviceVulkan11Features));
            Vulkan13 = (VkPhysicalDeviceVulkan13Features*)((byte*)Vulkan12 + sizeof(VkPhysicalDeviceVulkan12Features));
            *Features = new VkPhysicalDeviceFeatures2 { pNext = Vulkan11 };
            *Vulkan11 = new VkPhysicalDeviceVulkan11Features { pNext = Vulkan12 };
            *Vulkan12 = new VkPhysicalDeviceVulkan12Features { pNext = vulkan13 ? Vulkan13 : null };
            *Vulkan13 = new VkPhysicalDeviceVulkan13Features();
        }

        public Utf8Strings InstanceExtensions { get; }

        public Utf8Strings DeviceExtensions { get; }

        public VulkanVideoDecode? Video { get; }

        public VkQueueFlags GraphicsFlags { get; }

        public VkPhysicalDeviceFeatures2* Features { get; }

        private VkPhysicalDeviceVulkan11Features* Vulkan11 { get; }

        private VkPhysicalDeviceVulkan12Features* Vulkan12 { get; }

        private VkPhysicalDeviceVulkan13Features* Vulkan13 { get; }

        // タイムラインセマフォ（必須）と、YUV の画像（取り込んだ動画のフレーム）・デコードに使う機能（対応していれば）だけを有効にする
        public void EnableSupported(VkInstanceApi api, VkPhysicalDevice physical, bool wantVideo)
        {
            var supported11 = new VkPhysicalDeviceVulkan11Features();
            var supported12 = new VkPhysicalDeviceVulkan12Features();
            var supported13 = new VkPhysicalDeviceVulkan13Features();
            supported11.pNext = &supported12;
            supported12.pNext = Vulkan12->pNext is null ? null : &supported13;
            var supported = new VkPhysicalDeviceFeatures2 { pNext = &supported11 };
            api.vkGetPhysicalDeviceFeatures2(physical, &supported);

            Vulkan12->timelineSemaphore = true;
            Vulkan11->samplerYcbcrConversion = supported11.samplerYcbcrConversion;
            if (wantVideo)
            {
                Vulkan12->hostQueryReset = supported12.hostQueryReset;
                if (Vulkan12->pNext is not null)
                {
                    Vulkan13->synchronization2 = supported13.synchronization2;
                }
            }
        }

        public void Dispose()
        {
            InstanceExtensions.Dispose();
            DeviceExtensions.Dispose();
            NativeMemory.Free(_block);
        }
    }

    /// <summary>UTF-8 の文字列の配列（char**）。捨てるまで有効</summary>
    internal sealed class Utf8Strings : IDisposable
    {
        private readonly nint[] _strings;

        public Utf8Strings(IReadOnlyList<string> values)
        {
            Values = [.. values];
            _strings = [.. values.Select(Marshal.StringToCoTaskMemUTF8)];
            Count = values.Count;
            Pointer = Marshal.AllocCoTaskMem(Math.Max(1, Count) * sizeof(nint));
            for (var i = 0; i < Count; i++)
            {
                ((nint*)Pointer)[i] = _strings[i];
            }
        }

        public nint Pointer { get; }

        public int Count { get; }

        public IReadOnlyList<string> Values { get; }

        public void Dispose()
        {
            foreach (var value in _strings)
            {
                Marshal.FreeCoTaskMem(value);
            }

            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    private static HashSet<string> InstanceExtensions()
    {
        uint count = 0;
        vkEnumerateInstanceExtensionProperties(null, &count, null);
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* pointer = properties)
        {
            vkEnumerateInstanceExtensionProperties(null, &count, pointer);
        }

        return [.. properties.Select(p => Marshal.PtrToStringUTF8((nint)p.extensionName) ?? "")];
    }

    private static HashSet<string> InstanceLayers()
    {
        uint count = 0;
        vkEnumerateInstanceLayerProperties(&count, null);
        var properties = new VkLayerProperties[count];
        fixed (VkLayerProperties* pointer = properties)
        {
            vkEnumerateInstanceLayerProperties(&count, pointer);
        }

        return [.. properties.Select(p => Marshal.PtrToStringUTF8((nint)p.layerName) ?? "")];
    }

    private static HashSet<string> DeviceExtensions(VkInstanceApi api, VkPhysicalDevice device)
    {
        uint count = 0;
        api.vkEnumerateDeviceExtensionProperties(device, null, &count, null);
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* pointer = properties)
        {
            api.vkEnumerateDeviceExtensionProperties(device, null, &count, pointer);
        }

        return [.. properties.Select(p => Marshal.PtrToStringUTF8((nint)p.extensionName) ?? "")];
    }
}
