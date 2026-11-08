using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>ハードウェアデコードを使うか</summary>
public enum HardwareDecoding
{
    /// <summary>使えれば使い、使えなければソフトウェアでデコードする</summary>
    Auto,

    /// <summary>ソフトウェアだけでデコードする</summary>
    Disabled,
}

/// <summary>RGB への変換と縮小の質</summary>
public enum ConversionQuality
{
    /// <summary>色の丸めと色差の補間を丁寧に行う（書き出し用。1080p で 1 フレームあたり数ミリ秒遅い）</summary>
    Accurate,

    /// <summary>速さを優先する（プレビュー用）</summary>
    Fast,
}

/// <summary>デコードしたフレームの渡し方</summary>
public enum VideoFrameOutput
{
    /// <summary>BGRA の画像（swscale で RGB にし、縮小する）</summary>
    Bgra,

    /// <summary>
    /// 4:2:0 の YUV の平面のまま（NV12・P010。RGB にするのと縮小は、使う側が GPU で行う）。
    /// それ以外の形式（4:2:2・4:4:4・RGB・透明あり）は、BGRA の画像にする
    /// </summary>
    Planes,

    /// <summary>
    /// GPU の上のフレームのまま。Windows で VideoDecoderOptions.D3D11 があれば、d3d11va でデコードして共有のテクスチャに写す。
    /// VideoDecoderOptions.VulkanDevice があれば、そのデバイスで Vulkan Video でデコードする（試験的）。
    /// GPU でデコードできない動画・形式は、平面か BGRA の画像にする
    /// </summary>
    Gpu,
}

/// <summary>
/// デコーダーの設定。MaxWidth・MaxHeight を指定すると、縦横比を保ってその枠に収まる大きさに縮小する（拡大はしない。平面のまま渡すときは縮小しない）。
/// CacheCapacity は覚えておくフレームの数（逆再生や行ったり来たりのシークを速くする）
/// </summary>
public sealed record VideoDecoderOptions(
    int? MaxWidth = null,
    int? MaxHeight = null,
    HardwareDecoding Hardware = HardwareDecoding.Auto,
    int CacheCapacity = 32,
    ConversionQuality Quality = ConversionQuality.Accurate,
    VideoFrameOutput Output = VideoFrameOutput.Bgra,
    nint VulkanDevice = 0,
    D3D11VideoDevice? D3D11 = null);

/// <summary>
/// デコードしたフレーム。Time は素材の先頭からの表示時刻（秒）、Duration は次のフレームまでの長さ。
/// Image（BGRA）か Planes（YUV の平面）のどちらかがある
/// </summary>
public sealed record DecodedFrame(BgraImage? Image, Rational Time, Rational? Duration)
{
    /// <summary>平面のまま渡したときの YUV の平面（BGRA にしたときは null）</summary>
    public VideoPlanes? Planes { get; init; }

    /// <summary>GPU の上のフレームのまま渡したときのフレーム（デコーダーが持っている参照。使う側は Clone する）</summary>
    public HardwareVideoFrame? GpuFrame { get; init; }

    /// <summary>BGRA の画像（平面のまま渡したときは、CPU で RGB にする）</summary>
    public BgraImage Bgra => Image
        ?? (Planes is { } planes ? YuvConversion.ToBgra(planes) : throw new InvalidOperationException("GPU の上のフレームは、BGRA の画像として読めません"));

    /// <summary>
    /// フレームの色の情報（タグがなければ推測した値）。Image はこの行列と範囲で RGB にしたもので、
    /// 原色と伝達特性は変えていない（PQ・HLG の素材なら、PQ・HLG のまま 8 ビットにした値）
    /// </summary>
    public VideoColorInfo Color { get; init; } = VideoColorInfo.Srgb;
}

/// <summary>
/// 動画の 1 つの映像ストリームをデコードする。時刻を指定すると、その時刻に表示されているフレーム
/// （表示時刻がその時刻以前で最も遅いフレーム）を返す。フレームの選択はタイムスタンプで行うので、
/// 可変フレームレートの動画やフレームレートの違う動画でもずれない。
/// 前に進むときはシークせずにデコードを続け、後ろに戻るときはキーフレームから読み直してフレームを覚えておく。
/// スレッドセーフではない（使う側ごとに 1 つ作る）
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    // これより先へ進むときは、デコードを続けるよりシークしたほうが速い
    private static readonly Rational ForwardDecodeLimit = new(2, 1);

    // 前へ進むとき、目的のフレームの手前これだけのフレームは覚えておく。
    // 複数のスレッドが少しずつ順番を前後して近くのフレームを求めても、戻ってデコードし直さずに済む
    private const int NearbyFrames = 8;

    // シークした後、目的のフレームのこれだけ手前までは、ほかのフレームが参照しないフレーム（B フレームなど）をデコードしない
    private const int SkipMarginFrames = 4;

    private readonly string _path;
    private readonly VideoDecoderOptions _options;
    private readonly int _streamIndex;
    private readonly Rational _timeBase;
    private readonly long _startTimestamp;
    private readonly long _nominalFrameDuration;
    private readonly long _forwardDecodeLimit;
    private readonly LinkedList<CacheEntry> _cache = new();

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVBufferRef* _hardwareDevice;
    private AVPacket* _packet;
    private AVFrame* _received;
    private AVFrame* _held;
    private AVFrame* _transfer;
    private SwsContext* _scaler;
    private bool _hasHeld;
    private long _heldTimestamp;
    private bool _drainStarted;
    private bool _endOfStream;
    private long? _lastRequested;
    private long? _skipBefore;

    private VideoDecoder(string path, VideoDecoderOptions options)
    {
        _path = path;
        _options = options;
        try
        {
            AVFormatContext* format = null;
            var opened = ffmpeg.avformat_open_input(&format, path, null, null);
            if (opened < 0)
            {
                throw new MediaException($"メディアファイルとして開けません（{FfmpegErrors.Describe(opened)}）: {path}");
            }

            _format = format;
            ffmpeg.avformat_find_stream_info(_format, null).ThrowIfError("ストリームの情報の取得");

            AVCodec* decoder = null;
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (_streamIndex < 0 || decoder is null)
            {
                throw new MediaException($"映像が含まれていないか、デコードできない形式です: {path}");
            }

            var stream = _format->streams[_streamIndex];
            if (stream->time_base.num <= 0 || stream->time_base.den <= 0)
            {
                throw new MediaException($"映像のタイムベースが正しくありません: {path}");
            }

            _timeBase = new Rational(stream->time_base.num, stream->time_base.den);
            _startTimestamp = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
            var frameRate = stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0
                ? new Rational(stream->avg_frame_rate.num, stream->avg_frame_rate.den)
                : new Rational(30, 1);
            _nominalFrameDuration = Math.Max(1, ToTimestampDelta(frameRate.Reciprocal()));
            _forwardDecodeLimit = ToTimestampDelta(ForwardDecodeLimit);

            SourceWidth = stream->codecpar->width;
            SourceHeight = stream->codecpar->height;
            (OutputWidth, OutputHeight) = FitSize(SourceWidth, SourceHeight, options.MaxWidth, options.MaxHeight);

            OpenCodec(decoder, options.Hardware == HardwareDecoding.Auto);
            _packet = ffmpeg.av_packet_alloc();
            _received = ffmpeg.av_frame_alloc();
            _held = ffmpeg.av_frame_alloc();
            _transfer = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>素材の映像の大きさ</summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>返すフレームの大きさ</summary>
    public int OutputWidth { get; }

    public int OutputHeight { get; }

    /// <summary>使っているハードウェアデコードの種類（"d3d11va" など）。ソフトウェアなら null</summary>
    public string? HardwareDevice { get; private set; }

    /// <exception cref="MediaException">ファイルを開けない、または映像が含まれていない</exception>
    public static VideoDecoder Open(string path, VideoDecoderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new MediaException($"ファイルが見つかりません: {path}");
        }

        FfmpegLibrary.Load();
        return new VideoDecoder(path, options ?? new VideoDecoderOptions());
    }

    /// <summary>
    /// 指定した時刻（素材の先頭からの秒）に表示されているフレームを返す。
    /// 最初のフレームより前なら最初のフレーム、最後のフレームより後なら最後のフレームを返す
    /// </summary>
    public DecodedFrame GetFrame(Rational time)
    {
        ObjectDisposedException.ThrowIf(_format is null, this);

        var target = _startTimestamp + ToTimestamp(time);
        if (FindCached(target) is { } cached)
        {
            _lastRequested = target;
            return cached;
        }

        var backward = _lastRequested is { } last && target < last;
        _lastRequested = target;

        try
        {
            return Decode(target, backward);
        }
        catch (MediaException) when (HardwareDevice is not null)
        {
            // ハードウェアデコードが途中で失敗したら、ソフトウェアに切り替えてやり直す
            SwitchToSoftware();
            return Decode(target, backward);
        }
    }

    private DecodedFrame Decode(long target, bool backward)
    {
        // 持っているフレームから近い先なら、シークせずにデコードを続ける
        if (_hasHeld && _heldTimestamp <= target && target - _heldTimestamp <= _forwardDecodeLimit)
        {
            if (_endOfStream)
            {
                return CacheAndConvert(_held, _heldTimestamp, long.MaxValue);
            }

            // 手前のフレームを覚えるのは、ほぼ順番どおりに求められているとき（遠くへ進むときは変換の手間が無駄になる）
            var sequential = target - _heldTimestamp <= _nominalFrameDuration * NearbyFrames;
            if (DecodeUntil(target, backward, acceptLateStart: true, cacheNearby: sequential) is { } continued)
            {
                return continued;
            }
        }

        // シークした位置が目的の時刻より後ろになった場合は、もっと前からやり直す
        var seekTo = target;
        var step = _forwardDecodeLimit;
        for (var attempt = 0; ; attempt++)
        {
            Seek(seekTo);

            // 前へのシークでは、目的のフレームの近くまで参照されないフレームを飛ばして速くする
            // （後ろへ戻るときは、逆再生のためにすべてのフレームを覚えるので飛ばさない）
            _skipBefore = backward ? null : target - _nominalFrameDuration * SkipMarginFrames;
            var atStart = seekTo <= _startTimestamp || attempt >= 5;
            var frame = DecodeUntil(target, backward, acceptLateStart: atStart, cacheNearby: false);
            _skipBefore = null;
            if (frame is not null)
            {
                return frame;
            }

            seekTo = Math.Max(_startTimestamp, seekTo - step);
            step *= 2;
        }
    }

    // 目的の時刻を含むフレームまでデコードする。シーク直後の最初のフレームが目的の時刻より後ろで、
    // acceptLateStart でなければ null を返す（もっと前からシークし直す）。
    // cacheNearby なら、目的のフレームの少し手前のフレームも覚えておく
    private DecodedFrame? DecodeUntil(long target, bool cacheAll, bool acceptLateStart, bool cacheNearby)
    {
        while (true)
        {
            if (!ReceiveNextFrame())
            {
                if (!_hasHeld)
                {
                    throw new MediaException($"フレームを取り出せませんでした: {_path}");
                }

                return CacheAndConvert(_held, _heldTimestamp, long.MaxValue);
            }

            var timestamp = FrameTimestamp(_received);
            if (!_hasHeld)
            {
                if (timestamp > target && !acceptLateStart)
                {
                    ffmpeg.av_frame_unref(_received);
                    return null;
                }

                Hold(timestamp);
                if (timestamp > target)
                {
                    // 最初のフレームより前の時刻：最初のフレームを返す（次のフレームがまだ分からないので覚えない）
                    var (image, planes, gpuFrame, color) = Convert(_held);
                    var first = new DecodedFrame(image, ToSeconds(timestamp), null) { Color = color, Planes = planes, GpuFrame = gpuFrame };
                    if (gpuFrame is not null)
                    {
                        // GPU のフレームは、キャッシュに入れて、外すときに返す（次のフレームはまだ分からないので、時刻では見つからないようにする）
                        AddToCache(new CacheEntry(timestamp, timestamp, first));
                    }

                    return first;
                }

                continue;
            }

            if (timestamp <= _heldTimestamp)
            {
                // 時刻が戻るフレーム（壊れたファイルなど）は捨てる
                ffmpeg.av_frame_unref(_received);
                continue;
            }

            DecodedFrame? answer = null;
            var contains = _heldTimestamp <= target && target < timestamp;
            var nearby = cacheNearby && _heldTimestamp < target && target - _heldTimestamp <= _nominalFrameDuration * NearbyFrames;
            if (contains || cacheAll || nearby)
            {
                var converted = CacheAndConvert(_held, _heldTimestamp, timestamp);
                if (contains)
                {
                    answer = converted;
                }
            }

            Hold(timestamp);
            if (answer is not null)
            {
                return answer;
            }
        }
    }

    // 次のフレームを _received に受け取る。ファイルの終わりなら false
    private bool ReceiveNextFrame()
    {
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_codec, _received);
            if (received == 0)
            {
                return true;
            }

            if (received == ffmpeg.AVERROR_EOF)
            {
                _endOfStream = true;
                return false;
            }

            if (received != FfmpegErrors.TryAgain)
            {
                received.ThrowIfError("映像のデコード");
            }

            if (_drainStarted)
            {
                _endOfStream = true;
                return false;
            }

            var read = ffmpeg.av_read_frame(_format, _packet);
            if (read < 0)
            {
                // ファイルの終わり。デコーダーに残っているフレームを出し切る
                _drainStarted = true;
                ffmpeg.avcodec_send_packet(_codec, null);
                continue;
            }

            if (_packet->stream_index == _streamIndex)
            {
                _codec->skip_frame = _skipBefore is { } limit && _packet->pts != ffmpeg.AV_NOPTS_VALUE && _packet->pts < limit
                    ? AVDiscard.AVDISCARD_NONREF
                    : AVDiscard.AVDISCARD_DEFAULT;
                var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
                if (sent < 0 && sent != FfmpegErrors.TryAgain)
                {
                    ffmpeg.av_packet_unref(_packet);
                    sent.ThrowIfError("映像のデコード");
                }
            }

            ffmpeg.av_packet_unref(_packet);
        }
    }

    private void Seek(long timestamp)
    {
        ffmpeg.av_seek_frame(_format, _streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_codec);
        ffmpeg.av_frame_unref(_held);
        _hasHeld = false;
        _drainStarted = false;
        _endOfStream = false;
    }

    private void Hold(long timestamp)
    {
        ffmpeg.av_frame_unref(_held);
        ffmpeg.av_frame_move_ref(_held, _received);
        _hasHeld = true;
        _heldTimestamp = timestamp;
    }

    private long FrameTimestamp(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp != ffmpeg.AV_NOPTS_VALUE)
        {
            return timestamp;
        }

        // タイムスタンプがないフレームは、前のフレームの次として扱う
        return _hasHeld ? _heldTimestamp + _nominalFrameDuration : _startTimestamp;
    }

    private DecodedFrame CacheAndConvert(AVFrame* frame, long start, long end)
    {
        if (FindCachedExact(start) is { } existing)
        {
            return existing;
        }

        var (image, planes, gpuFrame, color) = Convert(frame);
        var decoded = new DecodedFrame(
            image,
            ToSeconds(start),
            end == long.MaxValue ? null : (end - start) * _timeBase)
        {
            Color = color,
            Planes = planes,
            GpuFrame = gpuFrame,
        };
        AddToCache(new CacheEntry(start, end, decoded));
        return decoded;
    }

    private void AddToCache(CacheEntry entry)
    {
        _cache.AddFirst(entry);
        while (_cache.Count > Math.Max(1, _options.CacheCapacity))
        {
            _cache.Last!.Value.Frame.GpuFrame?.Dispose();
            _cache.RemoveLast();
        }
    }

    private DecodedFrame? FindCached(long target)
    {
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Start <= target && target < node.Value.End)
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value.Frame;
            }
        }

        return null;
    }

    private DecodedFrame? FindCachedExact(long start)
    {
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Start == start)
            {
                return node.Value.Frame;
            }
        }

        return null;
    }

    // フレームを BGRA にする（平面のまま渡す設定で、渡せる形式なら平面にする。GPU のまま渡す設定で、Vulkan のフレームなら、そのまま渡す）。
    // ハードウェアデコードしたフレームは、先にメインメモリへ移す
    private (BgraImage? Image, VideoPlanes? Planes, HardwareVideoFrame? GpuFrame, VideoColorInfo Color) Convert(AVFrame* frame)
    {
        if (_options.Output == VideoFrameOutput.Gpu && frame->format == (int)AVPixelFormat.AV_PIX_FMT_VULKAN && frame->hw_frames_ctx is not null)
        {
            var frames = (AVHWFramesContext*)frame->hw_frames_ctx->data;
            VideoPlaneFormat? planeFormat = frames->sw_format switch
            {
                AVPixelFormat.AV_PIX_FMT_NV12 => VideoPlaneFormat.Nv12,
                AVPixelFormat.AV_PIX_FMT_P010LE => VideoPlaneFormat.P010,
                _ => null,
            };
            if (planeFormat is { } gpuFormat)
            {
                var gpuColor = FfmpegColor.FromFrame(frame, frames->sw_format, _format->streams[_streamIndex]->codecpar);
                var clone = ffmpeg.av_frame_clone(frame);
                if (clone is null)
                {
                    throw new MediaException("フレームの参照を作れませんでした");
                }

                return (null, null, new HardwareVideoFrame(clone, gpuFormat, gpuColor), gpuColor);
            }
        }

        // d3d11va でデコードしたフレームは、共有のテクスチャに写して渡す（空きがなければ、メインメモリに写す）
        if (_options.Output == VideoFrameOutput.Gpu && OperatingSystem.IsWindows() && _options.D3D11 is { } d3d11
            && frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 && frame->hw_frames_ctx is not null)
        {
            var frames = (AVHWFramesContext*)frame->hw_frames_ctx->data;
            VideoPlaneFormat? sharedFormat = frames->sw_format switch
            {
                AVPixelFormat.AV_PIX_FMT_NV12 => VideoPlaneFormat.Nv12,
                AVPixelFormat.AV_PIX_FMT_P010LE => VideoPlaneFormat.P010,
                _ => null,
            };
            if (sharedFormat is { } planeFormat && d3d11.CopyFrame(frame, planeFormat) is { } shared)
            {
                var sharedColor = FfmpegColor.FromFrame(frame, frames->sw_format, _format->streams[_streamIndex]->codecpar);
                return (null, null, new HardwareVideoFrame(shared, planeFormat, sharedColor, frame->width, frame->height), sharedColor);
            }
        }

        var source = frame;
        if (frame->hw_frames_ctx is not null)
        {
            ffmpeg.av_frame_unref(_transfer);
            ffmpeg.av_hwframe_transfer_data(_transfer, frame, 0).ThrowIfError("ハードウェアからの映像の取り出し");
            ffmpeg.av_frame_copy_props(_transfer, frame);
            source = _transfer;
        }

        var format = (AVPixelFormat)source->format;
        if (_options.Output is VideoFrameOutput.Planes or VideoFrameOutput.Gpu
            && CopyPlanes(source, format, FfmpegColor.FromFrame(source, format, _format->streams[_streamIndex]->codecpar)) is { } planes)
        {
            if (source == _transfer)
            {
                ffmpeg.av_frame_unref(_transfer);
            }

            return (null, planes, null, planes.Color);
        }

        _scaler = ffmpeg.sws_getCachedContext(
            _scaler,
            source->width, source->height, format,
            OutputWidth, OutputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)(_options.Quality == ConversionQuality.Accurate
                ? SwsFlags.SWS_BILINEAR | SwsFlags.SWS_ACCURATE_RND | SwsFlags.SWS_FULL_CHR_H_INT
                : SwsFlags.SWS_BILINEAR),
            null, null, null);
        if (_scaler is null)
        {
            throw new MediaException("映像の変換の準備に失敗しました");
        }

        var color = FfmpegColor.FromFrame(source, format, _format->streams[_streamIndex]->codecpar);
        var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        if (color.Matrix != VideoMatrix.Rgb)
        {
            // YUV の素材だけ、YUV → RGB の変換式を設定する
            ApplyColorspace(color, source->height);
        }

        var pixels = new byte[OutputWidth * OutputHeight * 4];
        fixed (byte* destination = pixels)
        {
            ffmpeg.sws_scale(
                _scaler,
                source->data.ToArray(), source->linesize.ToArray(), 0, source->height,
                [destination], [OutputWidth * 4]).ThrowIfError("映像の変換");
        }

        if (descriptor is not null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0)
        {
            PremultiplyAlpha(pixels);
        }

        if (frame == _transfer || source == _transfer)
        {
            ffmpeg.av_frame_unref(_transfer);
        }

        return (new BgraImage(OutputWidth, OutputHeight, pixels), null, null, color);
    }

    // 4:2:0 の YUV（8 ビット・10 ビット）を、NV12・P010 の平面に写す。ほかの形式なら null
    private static VideoPlanes? CopyPlanes(AVFrame* frame, AVPixelFormat format, VideoColorInfo color)
    {
        var width = frame->width;
        var height = frame->height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        switch (format)
        {
            case AVPixelFormat.AV_PIX_FMT_NV12:
            {
                var luma = CopyRows(frame->data[0], frame->linesize[0], width, height);
                var chroma = CopyRows(frame->data[1], frame->linesize[1], chromaWidth * 2, chromaHeight);
                return new VideoPlanes(VideoPlaneFormat.Nv12, width, height, luma, width, chroma, chromaWidth * 2, color);
            }

            case AVPixelFormat.AV_PIX_FMT_P010LE:
            {
                var luma = CopyRows(frame->data[0], frame->linesize[0], width * 2, height);
                var chroma = CopyRows(frame->data[1], frame->linesize[1], chromaWidth * 4, chromaHeight);
                return new VideoPlanes(VideoPlaneFormat.P010, width, height, luma, width * 2, chroma, chromaWidth * 4, color);
            }

            case AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P:
            {
                var luma = CopyRows(frame->data[0], frame->linesize[0], width, height);
                var chroma = new byte[chromaWidth * 2 * chromaHeight];
                for (var y = 0; y < chromaHeight; y++)
                {
                    var u = frame->data[1] + (long)y * frame->linesize[1];
                    var v = frame->data[2] + (long)y * frame->linesize[2];
                    var row = chroma.AsSpan(y * chromaWidth * 2, chromaWidth * 2);
                    for (var x = 0; x < chromaWidth; x++)
                    {
                        row[x * 2] = u[x];
                        row[x * 2 + 1] = v[x];
                    }
                }

                return new VideoPlanes(VideoPlaneFormat.Nv12, width, height, luma, width, chroma, chromaWidth * 2, color);
            }

            case AVPixelFormat.AV_PIX_FMT_YUV420P10LE:
            {
                // 値を上位の 10 ビットに移して、P010 と同じ並びにする
                var luma = new byte[width * 2 * height];
                for (var y = 0; y < height; y++)
                {
                    var source = (ushort*)(frame->data[0] + (long)y * frame->linesize[0]);
                    var row = MemoryMarshal.Cast<byte, ushort>(luma.AsSpan(y * width * 2, width * 2));
                    for (var x = 0; x < width; x++)
                    {
                        row[x] = (ushort)(source[x] << 6);
                    }
                }

                var chroma = new byte[chromaWidth * 4 * chromaHeight];
                for (var y = 0; y < chromaHeight; y++)
                {
                    var u = (ushort*)(frame->data[1] + (long)y * frame->linesize[1]);
                    var v = (ushort*)(frame->data[2] + (long)y * frame->linesize[2]);
                    var row = MemoryMarshal.Cast<byte, ushort>(chroma.AsSpan(y * chromaWidth * 4, chromaWidth * 4));
                    for (var x = 0; x < chromaWidth; x++)
                    {
                        row[x * 2] = (ushort)(u[x] << 6);
                        row[x * 2 + 1] = (ushort)(v[x] << 6);
                    }
                }

                return new VideoPlanes(VideoPlaneFormat.P010, width, height, luma, width * 2, chroma, chromaWidth * 4, color);
            }

            default:
                return null;
        }
    }

    private static byte[] CopyRows(byte* source, int sourceStride, int rowBytes, int rows)
    {
        var bytes = new byte[rowBytes * rows];
        for (var y = 0; y < rows; y++)
        {
            new ReadOnlySpan<byte>(source + (long)y * sourceStride, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
        }

        return bytes;
    }

    // YUV → RGB の変換式を、映像のタグ（BT.709・BT.601・BT.2020 など）と範囲（リミテッド・フル）に合わせる。
    // タグがない映像は、HD 以上なら BT.709、それ未満なら BT.601 とみなす（VideoColorInfo.Resolve を参照）
    private void ApplyColorspace(VideoColorInfo color, int height)
    {
        var fullRange = color.Range == VideoColorRange.Full;
        var coefficients = *(int4*)ffmpeg.sws_getCoefficients(FfmpegColor.SwsColorspace(color, height));
        ffmpeg.sws_setColorspaceDetails(_scaler, coefficients, fullRange ? 1 : 0, coefficients, 1, 0, 1 << 16, 1 << 16);
    }

    private static void PremultiplyAlpha(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
        }
    }

    private void OpenCodec(AVCodec* decoder, bool tryHardware)
    {
        // GPU のまま渡すときは、Windows では d3d11va（描画と同じ GPU のデバイス）でデコードし、共有のテクスチャに写して渡す（デバイスは借りるだけで、捨てない）
        if (_options.Output == VideoFrameOutput.Gpu && OperatingSystem.IsWindows() && _options.D3D11 is { } d3d11
            && SupportsDevice(decoder, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA))
        {
            try
            {
                OpenCodecContext(decoder, (AVBufferRef*)d3d11.Reference);
                HardwareDevice = "d3d11va";
                return;
            }
            catch (MediaException)
            {
                FreeCodec();
            }
        }

        // または、描画と同じ Vulkan のデバイスでデコードする（Vulkan Video）
        if (_options.Output == VideoFrameOutput.Gpu && _options.VulkanDevice != 0 && SupportsDevice(decoder, AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN))
        {
            try
            {
                OpenCodecContext(decoder, (AVBufferRef*)_options.VulkanDevice);
                HardwareDevice = "vulkan";
                return;
            }
            catch (MediaException)
            {
                FreeCodec();
            }
        }

        if (tryHardware && TryCreateHardwareDevice(decoder) is { } name)
        {
            try
            {
                OpenCodecContext(decoder, _hardwareDevice);
                HardwareDevice = name;
                return;
            }
            catch (MediaException)
            {
                FreeCodec();
                FreeHardwareDevice();
            }
        }

        OpenCodecContext(decoder, null);
        HardwareDevice = null;
    }

    private void OpenCodecContext(AVCodec* decoder, AVBufferRef* hardwareDevice)
    {
        _codec = ffmpeg.avcodec_alloc_context3(decoder);
        ffmpeg.avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar).ThrowIfError("デコーダーの設定");
        _codec->thread_count = 0; // CPU のコア数に合わせて自動で決める
        _codec->pkt_timebase = _format->streams[_streamIndex]->time_base;
        if (hardwareDevice is not null)
        {
            _codec->hw_device_ctx = ffmpeg.av_buffer_ref(hardwareDevice);

            // 覚えておくフレームと、描画が使っている途中のフレームの分も、GPU の画像を余分に用意する
            _codec->extra_hw_frames = Math.Max(1, _options.CacheCapacity) + 4;
        }

        ffmpeg.avcodec_open2(_codec, decoder, null).ThrowIfError("デコーダーの準備");
    }

    // OS ごとのハードウェアデコードを、デコーダーが対応していれば準備して _hardwareDevice に入れる。準備できた種類の名前を返す
    private string? TryCreateHardwareDevice(AVCodec* decoder)
    {
        string[] candidates = OperatingSystem.IsWindows() ? ["d3d11va"]
            : OperatingSystem.IsMacOS() ? ["videotoolbox"]
            : OperatingSystem.IsLinux() ? ["vaapi"]
            : [];

        foreach (var name in candidates)
        {
            var type = ffmpeg.av_hwdevice_find_type_by_name(name);
            if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE || !SupportsDevice(decoder, type))
            {
                continue;
            }

            string? deviceName = null;
            if (name == "vaapi")
            {
                // libva を読めて、DRM のデバイスを開けるときだけ使う（Vaapi を参照）
                deviceName = Vaapi.RenderNode;
                if (deviceName is null)
                {
                    continue;
                }
            }

            AVBufferRef* device = null;
            if (ffmpeg.av_hwdevice_ctx_create(&device, type, deviceName, null, 0) >= 0)
            {
                _hardwareDevice = device;
                return name;
            }
        }

        return null;
    }

    private static bool SupportsDevice(AVCodec* decoder, AVHWDeviceType type)
    {
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(decoder, i);
            if (config is null)
            {
                return false;
            }

            if ((config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 && config->device_type == type)
            {
                return true;
            }
        }
    }

    private void SwitchToSoftware()
    {
        var decoder = _codec->codec;
        FreeCodec();
        FreeHardwareDevice();
        OpenCodecContext(decoder, null);
        HardwareDevice = null;
        ffmpeg.av_frame_unref(_held);
        _hasHeld = false;
        _drainStarted = false;
        _endOfStream = false;
    }

    private long ToTimestamp(Rational seconds) => Rational.Rescale(1, seconds, _timeBase, Rounding.Down);

    private long ToTimestampDelta(Rational seconds) => Rational.Rescale(1, seconds, _timeBase, Rounding.Nearest);

    private Rational ToSeconds(long timestamp) => (timestamp - _startTimestamp) * _timeBase;

    private static (int Width, int Height) FitSize(int width, int height, int? maxWidth, int? maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            throw new MediaException("映像の大きさが分かりません");
        }

        var scale = Math.Min(
            maxWidth is { } w ? (double)w / width : 1,
            maxHeight is { } h ? (double)h / height : 1);
        scale = Math.Min(scale, 1);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private void FreeCodec()
    {
        if (_codec is not null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }
    }

    private void FreeHardwareDevice()
    {
        if (_hardwareDevice is not null)
        {
            var device = _hardwareDevice;
            ffmpeg.av_buffer_unref(&device);
            _hardwareDevice = null;
        }
    }

    public void Dispose()
    {
        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        foreach (var field in new[] { _received, _held, _transfer })
        {
            if (field is not null)
            {
                var frame = field;
                ffmpeg.av_frame_free(&frame);
            }
        }

        _received = _held = _transfer = null;

        foreach (var entry in _cache)
        {
            entry.Frame.GpuFrame?.Dispose();
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        FreeCodec();
        FreeHardwareDevice();

        if (_format is not null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }

        _cache.Clear();
    }

    private sealed record CacheEntry(long Start, long End, DecodedFrame Frame);
}
