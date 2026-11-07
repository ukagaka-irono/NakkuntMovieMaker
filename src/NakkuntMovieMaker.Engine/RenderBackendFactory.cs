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

/// <summary>
/// 描画の方式の設定。Device は使う GPU の名前（null なら自動で選ぶ）。
/// CrashMarkerPath を指定すると、GPU の準備の間だけそのファイルを置き、準備の途中でアプリが止まったら、次からは GPU を使わない
/// </summary>
/// <remarks>
/// ExperimentalVulkanVideo なら、GPU 版で、動画を描画と同じ GPU の Vulkan Video でデコードし、フレームをコピーせずに使う（試験的な機能。
/// 基準の PC では、FFmpeg とドライバーの不具合で落ちる動画があるので、既定では使わない）。
/// 使わないときは、ハードウェアでデコードしたフレームを YUV の平面のままメインメモリに写して GPU に送り、GPU で RGB にする
/// </remarks>
public sealed record RendererOptions(
    RendererPreference Preference = RendererPreference.Auto, string? Device = null, string? CrashMarkerPath = null, bool ExperimentalVulkanVideo = false)
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
    /// <summary>
    /// 自動のときに GPU を選ぶか。エフェクトとトランジションも GPU で描くようになった（M2 の手順 D）ので、
    /// 自動では GPU を試し、使えなければ（自己テストに通らない・ソフトウェアの Vulkan しかないなど）CPU を使う
    /// </summary>
    public static bool AutoUsesGpu { get; } = true;

    /// <param name="layouts">文字の配置のキャッシュ（大きさを測る処理と共有するとき）</param>
    public static RenderBackendSelection Create(RendererOptions? options = null, TextLayoutCache? layouts = null)
    {
        options ??= RendererOptions.Default;
        var wantsGpu = options.Preference == RendererPreference.Vulkan || (options.Preference == RendererPreference.Auto && AutoUsesGpu);
        if (!wantsGpu)
        {
            return new RenderBackendSelection(new SkiaCpuBackend(layouts: layouts), null);
        }

        if (options.CrashMarkerPath is { } marker && File.Exists(marker))
        {
            return Cpu(layouts, "前回、GPU の準備の途中でアプリが止まったので、CPU で描きます（設定の「描画」で GPU を選び直せます）");
        }

        // 準備の途中で止まったら（ドライバーの不具合など）、次の起動でわかるように目印を置いておく
        TryWriteMarker(options.CrashMarkerPath);
        try
        {
            VulkanBackend gpu;
            try
            {
                gpu = new VulkanBackend(VulkanDevice.Create(new VulkanDeviceOptions(options.Device, VideoDecode: options.ExperimentalVulkanVideo)), layouts, ownsDevice: true);
            }
            catch (VulkanException ex)
            {
                return Cpu(layouts, $"GPU（Vulkan）で描けません（{ex.Message}）。CPU で描きます");
            }

            if (GpuSelfTest.Run(gpu) is { } problem)
            {
                gpu.Dispose();
                return Cpu(layouts, $"{problem}。CPU で描きます");
            }

            var fallback = new FallbackBackend(gpu, new SkiaCpuBackend(layouts: layouts), ex => ex is VulkanException);
            return new RenderBackendSelection(fallback, null);
        }
        finally
        {
            TryDeleteMarker(options.CrashMarkerPath);
        }
    }

    private static RenderBackendSelection Cpu(TextLayoutCache? layouts, string reason) => new(new SkiaCpuBackend(layouts: layouts), reason);

    private static void TryWriteMarker(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteMarker(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
