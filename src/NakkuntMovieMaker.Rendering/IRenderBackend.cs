using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering;

/// <summary>
/// FramePlan を実行して、フレームの画像を作る。M1 は Skia の CPU 描画、M2 から Vulkan での描画を加える。
/// CPU 版は、GPU 版の結果が正しいかを比べるための基準としても残す
/// </summary>
public interface IRenderBackend
{
    /// <summary>
    /// plan を描く。scale はシーンの大きさに対する出力の大きさの倍率（プレビューの 1/2 なら 0.5）。
    /// 素材は media から取り出す
    /// </summary>
    BgraImage Render(FramePlan plan, IMediaSource media, double scale = 1, CancellationToken cancellationToken = default);
}

/// <summary>
/// 描くときに使う素材の画像を渡す。素材が見つからない（リンク切れなど）ときは null を返し、その素材は描かない。
/// scale は、素材の元の大きさに対して、描くのに十分な大きさの倍率（1 未満なら縮小した画像を返してよい）
/// </summary>
public interface IMediaSource
{
    /// <summary>動画の、素材の先頭から time 秒の時点に表示されているフレーム</summary>
    SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale);

    SourceImage? GetImage(AssetId asset, double scale);
}

/// <summary>
/// 素材の画像。Width・Height は素材の元の大きさ（ローカル座標の単位）で、Image はそれを縮小したものでもよい
/// </summary>
public sealed record SourceImage(BgraImage Image, int Width, int Height)
{
    public SourceImage(BgraImage image)
        : this(image, image.Width, image.Height)
    {
    }
}
