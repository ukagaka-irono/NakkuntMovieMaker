using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// 埋め込んだシェーダー（SPIR-V）が、どの GPU でも使える機能だけを使っているか。
/// Float16・Int16 などは、デバイスで有効にしないと使えず、対応していない GPU（MoltenVK・lavapipe など）では動かない
/// </summary>
public sealed class SpirvTests
{
    // OpCapability の値のうち、使ってよいもの（Shader だけ）
    private static readonly HashSet<uint> Allowed = [1];

    public static TheoryData<string> Shaders => new(typeof(VulkanBackend).Assembly.GetManifestResourceNames().Where(name => name.EndsWith(".spv", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Shaders))]
    public void UsesOnlyPortableCapabilities(string resource)
    {
        using var stream = typeof(VulkanBackend).Assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var words = MemoryMarshal.Cast<byte, uint>(memory.ToArray().AsSpan()).ToArray();
        Assert.Equal(0x07230203u, words[0]); // SPIR-V の目印

        var capabilities = new List<uint>();
        for (var i = 5; i < words.Length;)
        {
            var opcode = words[i] & 0xFFFF;
            var count = (int)(words[i] >> 16);
            if (opcode == 17) // OpCapability
            {
                capabilities.Add(words[i + 1]);
            }

            i += Math.Max(1, count);
        }

        Assert.All(capabilities, capability => Assert.Contains(capability, Allowed));
    }

    [Fact]
    public void EmbedsTheRendererShaders()
    {
        var names = typeof(VulkanBackend).Assembly.GetManifestResourceNames();
        Assert.Contains("NakkuntMovieMaker.Shaders.composite.spv", names);
        Assert.Contains("NakkuntMovieMaker.Shaders.clear.spv", names);
        Assert.Contains("NakkuntMovieMaker.Shaders.to_bgra8.spv", names);
    }
}
