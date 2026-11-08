using FFmpeg.AutoGen.Abstractions;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// GPU の上にある、デコードしたフレーム。描画の側が GPU の上のまま読む（メインメモリへ写さない）。次のどちらか。
/// <list type="bullet">
/// <item>Vulkan Video でデコードしたもの：FFmpeg の AVFrame（Frame。形式は AV_PIX_FMT_VULKAN）</item>
/// <item>d3d11va でデコードして、共有のテクスチャに写したもの（SharedTexture。Windows）</item>
/// </list>
/// 使い終わったら Dispose する（FFmpeg のフレームの参照・借りたテクスチャを返す）。別の場所でも使うときは Clone する
/// </summary>
public sealed unsafe class HardwareVideoFrame : IDisposable
{
    private nint _frame;
    private D3D11SharedTexture? _shared;
    private int _disposed;

    internal HardwareVideoFrame(AVFrame* frame, VideoPlaneFormat format, VideoColorInfo color)
    {
        _frame = (nint)frame;
        Format = format;
        Color = color;
        Width = frame->width;
        Height = frame->height;
    }

    internal HardwareVideoFrame(D3D11SharedTexture shared, VideoPlaneFormat format, VideoColorInfo color, int width, int height)
    {
        _shared = shared;
        Format = format;
        Color = color;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// FFmpeg の AVFrame（AVFrame*）。data[0] が AVVkFrame*、hw_frames_ctx が Vulkan のフレームの文脈。共有のテクスチャのフレームなら 0
    /// </summary>
    public nint Frame
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _frame;
        }
    }

    /// <summary>d3d11va でデコードして写した、共有のテクスチャ。Vulkan Video のフレームなら null</summary>
    public D3D11SharedTexture? SharedTexture
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _shared;
        }
    }

    /// <summary>画素の並び（NV12：8 ビット、P010：10 ビット）。GPU の画像は、輝度と色差の 2 つの平面を持つ</summary>
    public VideoPlaneFormat Format { get; }

    public VideoColorInfo Color { get; }

    /// <summary>映像の大きさ（共有のテクスチャは、これより大きいことがある）</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>同じフレームを指す、別の参照</summary>
    public HardwareVideoFrame Clone()
    {
        if (SharedTexture is { } shared)
        {
            shared.AddLease();
            return new HardwareVideoFrame(shared, Format, Color, Width, Height);
        }

        var clone = ffmpeg.av_frame_clone((AVFrame*)Frame);
        if (clone is null)
        {
            throw new MediaException("フレームの参照を作れませんでした");
        }

        return new HardwareVideoFrame(clone, Format, Color);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _shared, null)?.Return();
        var frame = (AVFrame*)Interlocked.Exchange(ref _frame, 0);
        if (frame is not null)
        {
            ffmpeg.av_frame_free(&frame);
        }
    }
}
