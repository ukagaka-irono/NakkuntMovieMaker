using NakkuntMovieMaker.Engine;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>描画の方式の選び方（自己テスト・前回止まったときの目印）</summary>
public sealed class SelectionTests
{
    [Fact]
    public void SelfTestPassesOnAWorkingGpu()
    {
        using var device = Gpu.CreateOrSkip();
        using var backend = new VulkanBackend(device);

        Assert.Null(GpuSelfTest.Run(backend));
    }

    [Fact]
    public void ChoosesTheGpuWhenAskedAndRemovesTheMarker()
    {
        using (Gpu.CreateOrSkip())
        {
        }

        using var folder = new TempFiles();
        var marker = folder.Combine("gpu-starting");
        var selection = RenderBackendFactory.Create(new RendererOptions(RendererPreference.Vulkan, CrashMarkerPath: marker));
        using var backend = selection.Backend;

        // CI の lavapipe（CPU で動く Vulkan）は、テストのときだけ許しているので、ここでは CPU に切り替わることがある
        if (selection.FallbackReason is null)
        {
            Assert.Equal(RenderBackendKind.Vulkan, backend.Info.Kind);
        }

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void UsesTheCpuAfterAStartupCrash()
    {
        using var folder = new TempFiles();
        var marker = folder.Write("gpu-starting", "2026-11-07");

        var selection = RenderBackendFactory.Create(new RendererOptions(RendererPreference.Vulkan, CrashMarkerPath: marker));
        using var backend = selection.Backend;

        Assert.Equal(RenderBackendKind.Cpu, backend.Info.Kind);
        Assert.Contains("前回", selection.FallbackReason);
        Assert.True(File.Exists(marker)); // 選び直すまで残す
    }

    [Fact]
    public void AutoUsesTheCpuUntilTheGpuDrawsEverything()
    {
        // エフェクトとトランジションを GPU に移すまで（M2 の手順 D）は、自動では CPU を使う
        var selection = RenderBackendFactory.Create();
        using var backend = selection.Backend;

        Assert.False(RenderBackendFactory.AutoUsesGpu);
        Assert.Equal((RenderBackendKind.Cpu, (string?)null), (backend.Info.Kind, selection.FallbackReason));
    }
}
