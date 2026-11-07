using FFmpeg.AutoGen.Abstractions;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// GPU（Vulkan）の上にある、デコードしたフレーム（FFmpeg の AVFrame。形式は AV_PIX_FMT_VULKAN）。
/// 描画の側が、同じ Vulkan のデバイスでそのまま読む（メインメモリへ写さない）。
/// 使い終わったら Dispose する（FFmpeg のフレームの参照を返す）。別の場所でも使うときは Clone する
/// </summary>
public sealed unsafe class HardwareVideoFrame : IDisposable
{
    private nint _frame;

    internal HardwareVideoFrame(AVFrame* frame, VideoPlaneFormat format, VideoColorInfo color)
    {
        _frame = (nint)frame;
        Format = format;
        Color = color;
        Width = frame->width;
        Height = frame->height;
    }

    /// <summary>FFmpeg の AVFrame（AVFrame*）。data[0] が AVVkFrame*、hw_frames_ctx が Vulkan のフレームの文脈</summary>
    public nint Frame
    {
        get
        {
            var frame = Volatile.Read(ref _frame);
            ObjectDisposedException.ThrowIf(frame == 0, this);
            return frame;
        }
    }

    /// <summary>画素の並び（NV12：8 ビット、P010：10 ビット）。GPU の画像は、輝度と色差の 2 つの平面を持つ</summary>
    public VideoPlaneFormat Format { get; }

    public VideoColorInfo Color { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>同じフレームを指す、別の参照</summary>
    public HardwareVideoFrame Clone()
    {
        var clone = ffmpeg.av_frame_clone((AVFrame*)Frame);
        if (clone is null)
        {
            throw new MediaException("フレームの参照を作れませんでした");
        }

        return new HardwareVideoFrame(clone, Format, Color);
    }

    public void Dispose()
    {
        var frame = (AVFrame*)Interlocked.Exchange(ref _frame, 0);
        if (frame is not null)
        {
            ffmpeg.av_frame_free(&frame);
        }
    }
}
