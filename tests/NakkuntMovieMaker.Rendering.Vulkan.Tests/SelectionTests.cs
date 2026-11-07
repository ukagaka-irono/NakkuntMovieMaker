using NakkuntMovieMaker.Engine;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>描画の方式の選び方（自己テスト・前回止まったときの目印）</summary>
public sealed class SelectionTests
{
    [Fact]
    public void SelfTestPassesOnAWorkingGpu()
    {
        using (Gpu.CreateOrSkip())
        {
        }

        // 自己テストが通らない GPU では、ここでスキップする（NMM_REQUIRE_VULKAN=1 なら失敗にする）
        Gpu.RequireRenderer();
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
    public void AutoTriesTheGpuAndFallsBackToTheCpu()
    {
        // 自動では GPU を試す。ソフトウェアの Vulkan（CI の lavapipe）や、自己テストに通らない GPU では、理由を付けて CPU を使う
        var selection = RenderBackendFactory.Create();
        using var backend = selection.Backend;

        Assert.True(RenderBackendFactory.AutoUsesGpu);
        if (backend.Info.Kind == RenderBackendKind.Cpu)
        {
            Assert.NotNull(selection.FallbackReason);
        }
        else
        {
            Assert.Null(selection.FallbackReason);
        }
    }
}
