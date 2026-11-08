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

    // Metal のシェーダーの言語（MSL）の予約語。macOS の MoltenVK は SPIR-V を MSL に変換するので、変数などの名前（OpName）に使うと、
    // 変換したシェーダーをコンパイルできず、パイプラインを作れない（composite の変数 device で起きた）
    private static readonly HashSet<string> MetalReserved =
    [
        "device", "constant", "thread", "threadgroup", "threadgroup_imageblock", "ray_data", "object_data",
        "kernel", "vertex", "fragment", "visible", "stage_in", "patch", "sampler", "texture",
    ];

    [Theory]
    [MemberData(nameof(Shaders))]
    public void AvoidsNamesReservedInMetal(string resource)
    {
        using var stream = typeof(VulkanBackend).Assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var words = MemoryMarshal.Cast<byte, uint>(memory.ToArray().AsSpan()).ToArray();

        var names = new List<string>();
        for (var i = 5; i < words.Length;)
        {
            var opcode = words[i] & 0xFFFF;
            var count = (int)Math.Max(1, words[i] >> 16);
            if (opcode is 5 or 6) // OpName（id と名前）・OpMemberName（id と番号と名前）
            {
                var start = opcode == 5 ? 2 : 3;
                var bytes = MemoryMarshal.AsBytes(words.AsSpan(i + start, count - start));
                var end = bytes.IndexOf((byte)0);
                names.Add(System.Text.Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]));
            }

            i += count;
        }

        Assert.DoesNotContain(names, MetalReserved.Contains);
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
