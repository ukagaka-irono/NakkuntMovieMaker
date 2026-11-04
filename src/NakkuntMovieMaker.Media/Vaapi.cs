using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// Linux の VA-API（ハードウェアデコード・エンコード）。同梱の Linux 版 FFmpeg は libva を初めて使うときに読み込み、
/// 見つからないとプロセスごと止まる（abort）。そのため、libva を読めて、DRM のレンダーノードを開けるときだけ、
/// そのノードを指定して使う（X11 は使わない）
/// </summary>
internal static class Vaapi
{
    private static readonly Lazy<string?> Node = new(FindRenderNode);

    /// <summary>使える DRM のレンダーノード（/dev/dri/renderD128 など）。Linux 以外、libva がない、開けるノードがないときは null</summary>
    public static string? RenderNode => OperatingSystem.IsLinux() ? Node.Value : null;

    private static string? FindRenderNode()
    {
        if (!NativeLibrary.TryLoad("libva.so.2", out _) || !NativeLibrary.TryLoad("libva-drm.so.2", out _))
        {
            return null;
        }

        try
        {
            var nodes = Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri", "renderD*") : [];
            Array.Sort(nodes, StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                try
                {
                    using var handle = File.OpenHandle(node, FileMode.Open, FileAccess.ReadWrite);
                    return node;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 権限がないノードは飛ばす
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
