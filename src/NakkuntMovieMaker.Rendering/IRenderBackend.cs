using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Color;

namespace NakkuntMovieMaker.Rendering;

/// <summary>描画の方式</summary>
public enum RenderBackendKind
{
    /// <summary>Skia の CPU 描画（GPU 版の結果が正しいかを比べる基準でもある）</summary>
    Cpu,

    /// <summary>Vulkan での GPU 描画</summary>
    Vulkan,
}

/// <summary>
/// 描画の方式の情報。PreferredConcurrency は、同時に描かせるとよいフレームの数（CPU 版はスレッドの数、GPU 版は同時に処理するフレームの数）
/// </summary>
public sealed record RenderBackendInfo(RenderBackendKind Kind, string Name, string? Device, int PreferredConcurrency);

/// <summary>描き方の指定。Scale はシーンの大きさに対する出力の大きさの倍率（プレビューの 1/2 なら 0.5）</summary>
public sealed record RenderRequest(double Scale = 1);

/// <summary>
/// FramePlan を実行して、フレームの画像を作る。M1 は Skia の CPU 描画、M2 から Vulkan での描画を加える。
/// CPU 版は、GPU 版の結果が正しいかを比べるための基準としても残す。複数のスレッドから同時に呼んでよい
/// </summary>
public interface IRenderBackend : IDisposable
{
    RenderBackendInfo Info { get; }

    /// <summary>plan を描く。素材は media から取り出す。結果は、使い終わったら破棄すること（GPU 版では GPU のメモリを持つ）</summary>
    RenderResult Render(FramePlan plan, IMediaSource media, RenderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 描いた結果。GPU 版では GPU のメモリにあり、ReadBgra でメインメモリに読み出す。破棄すると、持っている資源を返す
/// </summary>
public abstract class RenderResult : IDisposable
{
    public abstract int Width { get; }

    public abstract int Height { get; }

    /// <summary>premultiplied の BGRA（8 ビット）として読み出す。GPU 版では、描き終わるのを待つ</summary>
    public abstract BgraImage ReadBgra();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>メインメモリにある結果（CPU 版）</summary>
public sealed class CpuRenderResult(BgraImage image) : RenderResult
{
    public override int Width => image.Width;

    public override int Height => image.Height;

    public override BgraImage ReadBgra() => image;
}

public static class RenderBackendExtensions
{
    /// <summary>
    /// plan を描いて、BGRA の画像として受け取る。scale はシーンの大きさに対する出力の大きさの倍率
    /// </summary>
    public static BgraImage Render(this IRenderBackend backend, FramePlan plan, IMediaSource media, double scale = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        using var result = backend.Render(plan, media, new RenderRequest(scale), cancellationToken);
        return result.ReadBgra();
    }
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

    /// <summary>
    /// エフェクトのパラメーターで指定したファイル（LUT など）の場所。プロジェクトのフォルダからの相対パスなら、
    /// プロジェクトのフォルダを基準にする。既定では、そのまま返す
    /// </summary>
    string ResolvePath(string reference) => reference;

    /// <summary>ファイルの場所で指定した画像（立ち絵のパーツなど）。読めなければ null</summary>
    SourceImage? GetImageFile(string path, double scale) => null;

    /// <summary>
    /// GPU 版が使う：動画のフレームを、RGB にせず縮小もしない YUV の平面のまま。平面で渡せない（4:2:0 でないなど）か、
    /// 対応していなければ null（そのときは GetVideoFrame を使う）
    /// </summary>
    VideoPlaneFrame? GetVideoPlanes(AssetId asset, Rational time) => null;

    /// <summary>
    /// GPU 版が使う：動画のフレームを、描画と同じ GPU（device）でデコードしたフレームのまま（コピーしない）。
    /// GPU でデコードできない動画や、対応していなければ null（そのときは GetVideoPlanes・GetVideoFrame を使う）。
    /// 返したフレームは、使う側が Dispose する
    /// </summary>
    GpuVideoFrame? GetGpuVideoFrame(AssetId asset, Rational time, HardwareVideoDevice device) => null;
}

/// <summary>
/// 描画の GPU を、動画のデコードに使うためのもの（FFmpeg の Vulkan のデバイス）。Reference は FFmpeg の AVBufferRef*。
/// 描画をやめる前に Closing で知らせるので、このデバイスでデコードしているもの（デコーダー）は、それまでに捨てる
/// </summary>
public abstract class HardwareVideoDevice
{
    public abstract nint Reference { get; }

    public event Action? Closing;

    protected void OnClosing() => Closing?.Invoke();
}

/// <summary>GPU の上のままの動画のフレーム。Width・Height は素材の元の大きさ。Input は、RGB にした後にかける入力の色の変換</summary>
public sealed record GpuVideoFrame(HardwareVideoFrame Frame, int Width, int Height, ColorLut? Input) : IDisposable
{
    public void Dispose() => Frame.Dispose();
}

/// <summary>
/// 平面のままの動画のフレーム。Width・Height は素材の元の大きさ（ローカル座標の単位）。
/// Input は、RGB にした後にかける入力の色の変換（GetVideoFrame の画像にかかっているものと同じ。なければ null）
/// </summary>
public sealed record VideoPlaneFrame(VideoPlanes Planes, int Width, int Height, ColorLut? Input);

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

/// <summary>出力の画像の中の、画素の範囲（左上と大きさ）</summary>
public readonly record struct PixelRegion(int X, int Y, int Width, int Height);
