using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Rendering.Vulkan;
using NakkuntMovieMaker.Text;

namespace NakkuntMovieMaker.Engine;

/// <summary>描画の方式の選び方</summary>
public enum RendererPreference
{
    /// <summary>GPU を使えれば GPU、使えなければ CPU</summary>
    Auto,

    /// <summary>CPU（Skia）だけ</summary>
    Cpu,

    /// <summary>GPU（Vulkan）。使えなければ CPU に切り替え、理由を知らせる</summary>
    Vulkan,
}

/// <summary>描画の方式の設定。Device は使う GPU の名前（null なら自動で選ぶ）</summary>
public sealed record RendererOptions(RendererPreference Preference = RendererPreference.Auto, string? Device = null)
{
    public static RendererOptions Default { get; } = new();

    /// <summary>"auto"・"cpu"・"vulkan"（"gpu" も可）から読む。分からなければ null</summary>
    public static RendererPreference? ParsePreference(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => RendererPreference.Auto,
        "cpu" => RendererPreference.Cpu,
        "vulkan" or "gpu" => RendererPreference.Vulkan,
        _ => null,
    };
}

/// <summary>選んだ描画の方式。FallbackReason は、望んだ方式を使えずに切り替えたときの理由（利用者に表示できる文）</summary>
public sealed record RenderBackendSelection(IRenderBackend Backend, string? FallbackReason);

/// <summary>設定から描画の方式を作る（アプリ・書き出し・CLI・ベンチマークで共通）</summary>
public static class RenderBackendFactory
{
    /// <param name="layouts">文字の配置のキャッシュ（大きさを測る処理と共有するとき）</param>
    public static RenderBackendSelection Create(RendererOptions? options = null, TextLayoutCache? layouts = null)
    {
        options ??= RendererOptions.Default;

        // 自動のときに GPU を選ぶのは、GPU 版の自己テストを入れてから（M2 の手順 C-14）。それまでは、指定したときだけ使う
        if (options.Preference == RendererPreference.Vulkan)
        {
            try
            {
                var device = VulkanDevice.Create(new VulkanDeviceOptions(options.Device));
                try
                {
                    return new RenderBackendSelection(new VulkanBackend(device, layouts, ownsDevice: true), null);
                }
                catch
                {
                    device.Dispose();
                    throw;
                }
            }
            catch (VulkanException ex)
            {
                return new RenderBackendSelection(new SkiaCpuBackend(layouts: layouts), $"GPU（Vulkan）で描けません（{ex.Message}）。CPU で描きます");
            }
        }

        return new RenderBackendSelection(new SkiaCpuBackend(layouts: layouts), null);
    }
}
