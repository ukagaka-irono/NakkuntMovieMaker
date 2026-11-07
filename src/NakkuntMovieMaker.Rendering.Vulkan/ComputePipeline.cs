using System.Collections.Concurrent;
using System.Reflection;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>シェーダーに渡すものの種類（binding の順に並べる）</summary>
public enum GpuBindingKind
{
    /// <summary>読み書きするバッファー（StructuredBuffer・RWStructuredBuffer）</summary>
    StorageBuffer,

    /// <summary>読み書きする画像（RWTexture2D。レイアウトは General）</summary>
    StorageImage,

    /// <summary>サンプラーで読む画像（Sampler2D。レイアウトは ShaderReadOnlyOptimal）</summary>
    SampledImage,
}

/// <summary>シェーダーに渡すもの。バッファーか画像のどちらか</summary>
public readonly record struct GpuBinding(GpuBuffer? Buffer = null, GpuImage? Image = null)
{
    public static implicit operator GpuBinding(GpuBuffer buffer) => new(Buffer: buffer);

    public static implicit operator GpuBinding(GpuImage image) => new(Image: image);
}

/// <summary>
/// コンピュートシェーダー（SPIR-V）のパイプライン。Dispatch で、渡すものとプッシュ定数を指定して実行を記録する（GPU のスレッドで）
/// </summary>
public sealed unsafe class ComputePipeline : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly GpuBindingKind[] _bindings;
    private readonly int _pushConstantBytes;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly VkSampler _sampler;

    internal ComputePipeline(VulkanDevice device, ReadOnlySpan<byte> spirv, GpuBindingKind[] bindings, int pushConstantBytes, VkPipelineCache cache, VkSampler sampler)
    {
        _device = device;
        _bindings = bindings;
        _pushConstantBytes = pushConstantBytes;
        _sampler = sampler;
        var api = device.DeviceApi;

        var layoutBindings = stackalloc VkDescriptorSetLayoutBinding[Math.Max(1, bindings.Length)];
        for (var i = 0; i < bindings.Length; i++)
        {
            layoutBindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)i,
                descriptorType = DescriptorType(bindings[i]),
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute,
            };
        }

        var setInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = (uint)bindings.Length, pBindings = layoutBindings };
        VkDescriptorSetLayout setLayout;
        Check(api.vkCreateDescriptorSetLayout(&setInfo, null, &setLayout), "ディスクリプターの準備");
        _setLayout = setLayout;

        var range = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = (uint)pushConstantBytes };
        var layoutInfo = new VkPipelineLayoutCreateInfo
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = pushConstantBytes > 0 ? 1u : 0u,
            pPushConstantRanges = &range,
        };
        VkPipelineLayout layout;
        Check(api.vkCreatePipelineLayout(&layoutInfo, null, &layout), "パイプラインの準備");
        _layout = layout;

        VkShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)code };
            Check(api.vkCreateShaderModule(&moduleInfo, null, &module), "シェーダーの読み込み");
        }

        try
        {
            var entry = new VkUtf8ReadOnlyString("main"u8);
            var pipelineInfo = new VkComputePipelineCreateInfo
            {
                stage = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Compute, module = module, pName = entry },
                layout = layout,
            };
            VkPipeline pipeline;
            Check(api.vkCreateComputePipelines(cache, 1, &pipelineInfo, null, &pipeline), "パイプラインの作成");
            _pipeline = pipeline;
        }
        finally
        {
            api.vkDestroyShaderModule(module, null);
        }
    }

    /// <summary>
    /// シェーダーの実行を記録する。bindings はパイプラインを作ったときの順。groups は (x, y, z) のワークグループの数。
    /// 画像は、あらかじめ正しいレイアウト（StorageImage は General、SampledImage は ShaderReadOnlyOptimal）にしておくこと
    /// </summary>
    public void Dispatch<TPush>(GpuCommandContext context, ReadOnlySpan<GpuBinding> bindings, in TPush push, uint groupsX, uint groupsY, uint groupsZ = 1)
        where TPush : unmanaged
    {
        if (sizeof(TPush) != _pushConstantBytes)
        {
            throw new ArgumentException($"プッシュ定数の大きさが違います（{sizeof(TPush)}、パイプラインは {_pushConstantBytes}）", nameof(push));
        }

        if (bindings.Length != _bindings.Length)
        {
            throw new ArgumentException($"渡すものの数が違います（{bindings.Length}、パイプラインは {_bindings.Length}）", nameof(bindings));
        }

        var api = _device.DeviceApi;
        var set = context.AllocateDescriptorSet(_setLayout);
        var writes = stackalloc VkWriteDescriptorSet[Math.Max(1, bindings.Length)];
        var buffers = stackalloc VkDescriptorBufferInfo[Math.Max(1, bindings.Length)];
        var images = stackalloc VkDescriptorImageInfo[Math.Max(1, bindings.Length)];
        for (var i = 0; i < bindings.Length; i++)
        {
            var kind = _bindings[i];
            writes[i] = new VkWriteDescriptorSet { dstSet = set, dstBinding = (uint)i, descriptorCount = 1, descriptorType = DescriptorType(kind) };
            if (kind == GpuBindingKind.StorageBuffer)
            {
                var buffer = bindings[i].Buffer ?? throw new ArgumentException($"binding {i} にはバッファーを渡してください", nameof(bindings));
                buffers[i] = new VkDescriptorBufferInfo { buffer = buffer.Buffer, offset = 0, range = Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE };
                writes[i].pBufferInfo = &buffers[i];
            }
            else
            {
                var image = bindings[i].Image ?? throw new ArgumentException($"binding {i} には画像を渡してください", nameof(bindings));
                images[i] = new VkDescriptorImageInfo
                {
                    sampler = kind == GpuBindingKind.SampledImage ? _sampler : VkSampler.Null,
                    imageView = image.View,
                    imageLayout = kind == GpuBindingKind.StorageImage ? VkImageLayout.General : VkImageLayout.ShaderReadOnlyOptimal,
                };
                writes[i].pImageInfo = &images[i];
            }
        }

        api.vkUpdateDescriptorSets((uint)bindings.Length, writes, 0, null);
        var commands = context.CommandBuffer;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, _pipeline);
        api.vkCmdBindDescriptorSets(commands, VkPipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        if (_pushConstantBytes > 0)
        {
            var copy = push;
            api.vkCmdPushConstants(commands, _layout, VkShaderStageFlags.Compute, 0, (uint)_pushConstantBytes, &copy);
        }

        api.vkCmdDispatch(commands, groupsX, groupsY, groupsZ);
    }

    /// <summary>size を group の大きさで割り、切り上げる（ワークグループの数）</summary>
    public static uint Groups(int size, int group) => (uint)((size + group - 1) / group);

    public void Dispose()
    {
        var api = _device.DeviceApi;
        api.vkDestroyPipeline(_pipeline, null);
        api.vkDestroyPipelineLayout(_layout, null);
        api.vkDestroyDescriptorSetLayout(_setLayout, null);
    }

    private static VkDescriptorType DescriptorType(GpuBindingKind kind) => kind switch
    {
        GpuBindingKind.StorageBuffer => VkDescriptorType.StorageBuffer,
        GpuBindingKind.StorageImage => VkDescriptorType.StorageImage,
        _ => VkDescriptorType.CombinedImageSampler,
    };

    private static void Check(VkResult result, string what)
    {
        if (result != VkResult.Success)
        {
            throw new VulkanException($"{what}に失敗しました（{result}）");
        }
    }
}

/// <summary>
/// 埋め込んだシェーダー（Shaders.targets が SPIR-V にしたもの）からパイプラインを作り、覚えておく。
/// パイプラインキャッシュ（ドライバーがコンパイルした結果）はファイルに残し、次に起動したときに使い回す。
/// 画像を読むサンプラーは 1 つで、線形の補間・端は伸ばす
/// </summary>
public sealed unsafe class ShaderLibrary : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly ConcurrentDictionary<(Assembly Assembly, string Name), ComputePipeline> _pipelines = new();
    private readonly VkPipelineCache _cache;
    private readonly string? _cachePath;

    /// <param name="cachePath">パイプラインキャッシュのファイル（null なら残さない）</param>
    public ShaderLibrary(VulkanDevice device, string? cachePath = null)
    {
        _device = device;
        _cachePath = cachePath;
        var initial = cachePath is not null && File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : [];
        fixed (byte* data = initial)
        {
            // 古い・別の GPU のキャッシュは、ドライバーが無視する
            var info = new VkPipelineCacheCreateInfo { initialDataSize = (nuint)initial.Length, pInitialData = data };
            VkPipelineCache cache;
            if (device.DeviceApi.vkCreatePipelineCache(&info, null, &cache) != VkResult.Success)
            {
                info.initialDataSize = 0;
                device.DeviceApi.vkCreatePipelineCache(&info, null, &cache);
            }

            _cache = cache;
        }

        var samplerInfo = new VkSamplerCreateInfo
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            maxLod = 0,
        };
        VkSampler sampler;
        device.DeviceApi.vkCreateSampler(&samplerInfo, null, &sampler);
        LinearSampler = sampler;
    }

    public VkSampler LinearSampler { get; }

    /// <summary>埋め込んだシェーダー（assembly の NakkuntMovieMaker.Shaders.&lt;name&gt;.spv）のパイプライン</summary>
    public ComputePipeline Get(Assembly assembly, string name, GpuBindingKind[] bindings, int pushConstantBytes) =>
        _pipelines.GetOrAdd((assembly, name), key =>
        {
            using var stream = key.Assembly.GetManifestResourceStream($"NakkuntMovieMaker.Shaders.{key.Name}.spv")
                ?? throw new VulkanException($"シェーダーが見つかりません: {key.Name}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return new ComputePipeline(_device, memory.ToArray(), bindings, pushConstantBytes, _cache, LinearSampler);
        });

    /// <summary>このライブラリ（描画の方式）のシェーダー</summary>
    public ComputePipeline Get(string name, GpuBindingKind[] bindings, int pushConstantBytes) =>
        Get(typeof(ShaderLibrary).Assembly, name, bindings, pushConstantBytes);

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values)
        {
            pipeline.Dispose();
        }

        _pipelines.Clear();
        SaveCache();
        _device.DeviceApi.vkDestroyPipelineCache(_cache, null);
        _device.DeviceApi.vkDestroySampler(LinearSampler, null);
    }

    private void SaveCache()
    {
        if (_cachePath is null)
        {
            return;
        }

        try
        {
            nuint size = 0;
            _device.DeviceApi.vkGetPipelineCacheData(_cache, &size, null);
            var data = new byte[(int)size];
            fixed (byte* pointer = data)
            {
                _device.DeviceApi.vkGetPipelineCacheData(_cache, &size, pointer);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllBytes(temp, data.AsSpan(0, (int)size).ToArray());
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // キャッシュを残せなくても、次に起動したときに少し遅いだけ
        }
    }
}
