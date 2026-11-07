namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// GPU（Vulkan）が必要なテストの前提。使える GPU がなければスキップする。
/// CI（Linux の lavapipe）では NMM_REQUIRE_VULKAN=1 にして、スキップで失敗を見逃さないようにする。
/// テストでは、CPU で動く Vulkan も使い、検証レイヤーがあれば使う
/// </summary>
internal static class Gpu
{
    public static VulkanDeviceOptions Options { get; } = new(AllowSoftware: true, Validation: true);

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
