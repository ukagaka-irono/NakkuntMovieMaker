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
/// （CI のテスト用。環境変数 NMM_VULKAN_ALLOW_SOFTWARE=1 でも同じ）。Validation なら検証レイヤーを使う（NMM_VULKAN_VALIDATION=1 でも同じ）
/// </summary>
public sealed record VulkanDeviceOptions(string? DeviceName = null, bool AllowSoftware = false, bool Validation = false)
{
    public static VulkanDeviceOptions Default { get; } = new();

    internal bool SoftwareAllowed => AllowSoftware || Environment.GetEnvironmentVariable("NMM_VULKAN_ALLOW_SOFTWARE") == "1";

    internal bool ValidationEnabled => Validation || Environment.GetEnvironmentVariable("NMM_VULKAN_VALIDATION") == "1";
}

/// <summary>Vulkan を使えなかった。Message は利用者に表示できる文</summary>
public sealed class VulkanException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Vulkan のインスタンス・GPU・デバイス・キュー。グラフィックス・コンピュートのキューは 1 つで、Skia と自前のシェーダーで共有する
/// （キューへの送信は、GpuScheduler の 1 本のスレッドだけが行う）
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    // Vulkan 1.2 以上と、タイムラインセマフォ・RGBA16F の読み書きが必要
    private static readonly VkVersion MinimumApi = VkVersion.Version_1_2;
    private static readonly ConcurrentQueue<string> Messages = new();
    private static readonly Lock LoaderGate = new();
    private static VkResult? _loaded;

    private readonly VkDebugUtilsMessengerEXT _messenger;
    private bool _disposed;

    private VulkanDevice(VkInstance instance, VkInstanceApi instanceApi, VkPhysicalDevice physical, VulkanDeviceInfo info, VkDevice device, VkDeviceApi deviceApi,
        uint queueFamily, VkQueue queue, VkDebugUtilsMessengerEXT messenger)
    {
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
        var (instance, api, messenger) = CreateInstance(options);
        try
        {
            var candidates = Enumerate(instance, api);
            var chosen = Choose(candidates, options)
                ?? throw new VulkanException(candidates.Count == 0
                    ? "Vulkan に対応した GPU が見つかりません"
                    : $"使える GPU がありません（{string.Join("、", candidates.Select(c => $"{c.Info.Name}: {c.Info.Unsuitable}"))}）");
            return CreateDevice(instance, api, messenger, chosen.Physical, chosen.Info, chosen.QueueFamily);
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
            var (instance, api, messenger) = CreateInstance(VulkanDeviceOptions.Default with { Validation = false });
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

    private static (VkInstance Instance, VkInstanceApi Api, VkDebugUtilsMessengerEXT Messenger) CreateInstance(VulkanDeviceOptions options)
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

        return (instance, api, messenger);
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

    private static VulkanDevice CreateDevice(VkInstance instance, VkInstanceApi api, VkDebugUtilsMessengerEXT messenger, VkPhysicalDevice physical, VulkanDeviceInfo info, uint family)
    {
        var extensions = new List<string>();
        if (DeviceExtensions(api, physical).Contains("VK_KHR_portability_subset"))
        {
            extensions.Add("VK_KHR_portability_subset");
        }

        var priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo { queueFamilyIndex = family, queueCount = 1, pQueuePriorities = &priority };
        var vulkan12 = new VkPhysicalDeviceVulkan12Features { timelineSemaphore = true };
        var features = new VkPhysicalDeviceFeatures2 { pNext = &vulkan12 };
        using var extensionNames = new VkStringArray(extensions);
        var createInfo = new VkDeviceCreateInfo
        {
            pNext = &features,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
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
        return new VulkanDevice(instance, api, physical, info, device, deviceApi, family, queue, messenger);
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
