namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// GPU（Vulkan）が必要なテストの前提。使える GPU がなければスキップする。
/// CI（Linux の lavapipe）では NMM_REQUIRE_VULKAN=1 にして、スキップで失敗を見逃さないようにする。
/// テストでは、CPU で動く Vulkan も使い、検証レイヤーがあれば使う
/// </summary>
internal static class Gpu
{
    public static VulkanDeviceOptions Options { get; } = new(AllowSoftware: true, Validation: true);

    private static readonly Lazy<string?> SelfTestProblem = new(() =>
    {
        try
        {
            using var backend = new VulkanBackend(VulkanDevice.Create(Options), ownsDevice: true);
            return GpuSelfTest.Run(backend);
        }
        catch (VulkanException ex)
        {
            return ex.Message;
        }
    });

    /// <summary>
    /// 描画（VulkanBackend）を使うテストの前提。デバイスは作れても、自己テストが通らない GPU（CI の macOS の仮想の GPU など）では、
    /// アプリは CPU で描くので、テストもスキップする（NMM_REQUIRE_VULKAN=1 なら失敗にする）
    /// </summary>
    public static void RequireRenderer()
    {
        if (SelfTestProblem.Value is { } problem)
        {
            var required = Environment.GetEnvironmentVariable("NMM_REQUIRE_VULKAN") == "1";
            Assert.SkipWhen(!required, $"GPU で正しく描けないためスキップします（{problem}）");
            Assert.Fail(problem);
        }
    }

    public static VulkanDevice CreateOrSkip()
    {
        try
        {
            return VulkanDevice.Create(Options);
        }
        catch (VulkanException ex)
        {
            var required = Environment.GetEnvironmentVariable("NMM_REQUIRE_VULKAN") == "1";
            Assert.SkipWhen(!required, $"GPU（Vulkan）を使えないためスキップします（{ex.Message}）");
            throw;
        }
    }
}
