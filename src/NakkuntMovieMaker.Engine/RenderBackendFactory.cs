using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Skia;
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
        var cpu = new SkiaCpuBackend(layouts: layouts);
        return options.Preference switch
        {
            // GPU 版（Vulkan）は M2 の手順 C で入れる。それまでは CPU を使う
            RendererPreference.Vulkan => new RenderBackendSelection(cpu, "GPU（Vulkan）での描画は、まだ使えません。CPU で描きます"),
            _ => new RenderBackendSelection(cpu, null),
        };
    }
}
