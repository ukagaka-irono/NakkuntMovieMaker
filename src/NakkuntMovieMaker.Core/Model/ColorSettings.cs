namespace NakkuntMovieMaker.Core.Model;

/// <summary>合成（重ねる・混ぜる・ぼかす）を計算する色空間</summary>
public enum CompositingSpace
{
    /// <summary>sRGB のガンマのまま（M1・AviUtl・YMM4 と同じ見た目）</summary>
    Srgb,

    /// <summary>リニアの Rec.709（光の量として正しく混ざる。半透明やぼかしの縁が明るめになる）</summary>
    Linear,
}

/// <summary>
/// プロジェクトの色の設定。既定値は M1 と同じ処理（sRGB で合成し、色の変換をしない）。
/// 色空間の名前は OpenColorIO の構成の中の名前
/// </summary>
/// <param name="Compositing">合成の色空間</param>
/// <param name="OcioConfig">OpenColorIO の組み込みの構成の、版付きの名前。null なら、このソフトの既定（読み込んだときの版を保存し直すことはしない）</param>
/// <param name="ExportColorSpace">書き出すときに変換する先の色空間。null なら変換しない（BT.709 として書き出す）</param>
public sealed record ProjectColorSettings(
    CompositingSpace Compositing = CompositingSpace.Srgb,
    string? OcioConfig = null,
    string? ExportColorSpace = null)
{
    public static ProjectColorSettings Default { get; } = new();
}

/// <summary>プロキシ（編集用の軽い動画）を使う範囲</summary>
public enum ProxyMode
{
    /// <summary>使わない</summary>
    Off,

    /// <summary>1080p より大きい動画だけ</summary>
    LargeVideos,

    /// <summary>すべての動画</summary>
    AllVideos,
}

/// <summary>プロキシの設定。プロキシはプレビューだけで使い、書き出しでは必ず元の素材を使う</summary>
public sealed record ProjectProxySettings(ProxyMode Mode = ProxyMode.LargeVideos)
{
    public static ProjectProxySettings Default { get; } = new();
}
