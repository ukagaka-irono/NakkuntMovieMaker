using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>映像の圧縮形式</summary>
public enum VideoCodecKind
{
    H264,
    Hevc,
}

/// <summary>エンコーダーの選び方</summary>
public enum EncoderPreference
{
    /// <summary>ハードウェアエンコーダーを先に試し、使えなければソフトウェア</summary>
    Auto,

    /// <summary>ソフトウェアエンコーダーだけ（どの環境でも同じ結果になりやすい）</summary>
    Software,
}

/// <summary>映像の設定。大きさは偶数にすること（YUV 4:2:0 のため）</summary>
public sealed record VideoEncoderSettings(
    VideoCodecKind Codec,
    int Width,
    int Height,
    Rational FrameRate,
    int BitrateKbps,
    EncoderPreference Preference = EncoderPreference.Auto);

/// <summary>音声の設定（AAC・ステレオ）</summary>
public sealed record AudioEncoderSettings(int SampleRate = 48000, int BitrateKbps = 192);

/// <summary>
/// MP4（映像と音声）を書く。映像は BGRA の画像を 1 枚ずつ、音声はステレオの float32 を順に渡す。
/// 映像のエンコーダーは、OS ごとの候補を順に開いてみて、最初に開けたものを使う
/// （Windows：QSV → NVENC → AMF → Media Foundation、macOS：VideoToolbox、最後はソフトウェア）。
/// 色は BT.709・リミテッドレンジとしてタグを付け、MP4 は faststart（先頭に索引を置く）で書く
/// </summary>
public sealed unsafe class MediaWriter : IMediaFileWriter
{
    private readonly string _path;
    private AVFormatContext* _format;
    private AVCodecContext* _video;
    private AVCodecContext* _audio;
    private AVStream* _videoStream;
    private AVStream* _audioStream;
    private AVFrame* _videoFrame;
    private AVFrame* _audioFrame;
    private AVPacket* _packet;
    private SwsContext* _scaler;
    private long _videoPts;
    private long _audioPts;
    private float[] _audioPending = [];
    private int _audioPendingFrames;
    private bool _finished;

    private MediaWriter(string path)
    {
        _path = path;
    }

    /// <summary>使っている映像のエンコーダーの名前（"h264_qsv" など）</summary>
    public string? VideoEncoder { get; private set; }

    /// <summary>
    /// 書き出しを始める。video・audio のどちらかは指定すること
    /// </summary>
    /// <exception cref="MediaException">ファイルを作れない、または使えるエンコーダーがない</exception>
    public static MediaWriter Create(string path, VideoEncoderSettings? video, AudioEncoderSettings? audio)
    {
        if (video is null && audio is null)
        {
            throw new ArgumentException("映像か音声を指定してください");
        }

        if (video is { } v && (v.Width <= 0 || v.Height <= 0 || v.Width % 2 != 0 || v.Height % 2 != 0))
        {
            throw new ArgumentException("映像の幅と高さは、正の偶数にしてください", nameof(video));
        }

        FfmpegLibrary.Load();
        var writer = new MediaWriter(path);
        try
        {
            writer.Open(video, audio);
            return writer;
        }
        catch
        {
            writer.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// 候補のエンコーダーのうち、この環境で開けるものの名前（順番どおり）。開けるかを実際に試すので少し時間がかかる
    /// </summary>
    public static IReadOnlyList<string> AvailableEncoders(VideoCodecKind codec, EncoderPreference preference = EncoderPreference.Auto)
    {
        FfmpegLibrary.Load();
        var settings = new VideoEncoderSettings(codec, 256, 144, 30, 1000, preference);
        var available = new List<string>();
        foreach (var name in Candidates(codec, preference))
        {
            var context = TryOpenVideo(name, settings, globalHeader: false);
            if (context is not null)
            {
                ffmpeg.avcodec_free_context(&context);
                available.Add(name);
            }
        }

        return available;
    }

    /// <summary>映像の 1 フレーム（premultiplied の BGRA）を書く。大きさは設定と同じにすること</summary>
    public void WriteVideo(BgraImage image)
    {
        ObjectDisposedException.ThrowIf(_format is null, this);
        if (_video is null)
        {
            throw new InvalidOperationException("映像なしで作った書き出しです");
        }

        if (image.Width != _video->width || image.Height != _video->height)
        {
            throw new ArgumentException($"画像の大きさが違います（{image.Width}×{image.Height}、設定は {_video->width}×{_video->height}）", nameof(image));
        }

        ffmpeg.av_frame_make_writable(_videoFrame).ThrowIfError("映像のフレームの準備");
        fixed (byte* pixels = image.Pixels)
        {
            byte*[] source = [pixels];
            int[] stride = [image.Stride];
            ffmpeg.sws_scale(_scaler, source, stride, 0, image.Height, _videoFrame->data.ToArray(), _videoFrame->linesize.ToArray()).ThrowIfError("映像の変換");
        }

        _videoFrame->pts = _videoPts++;
        Encode(_video, _videoStream, _videoFrame);
    }

    /// <summary>音声（ステレオのインターリーブ、-1〜1）を書く。エンコーダーの 1 フレーム分ずつまとめて送る</summary>
    public void WriteAudio(ReadOnlySpan<float> stereo)
    {
        ObjectDisposedException.ThrowIf(_format is null, this);
        if (_audio is null)
        {
            throw new InvalidOperationException("音声なしで作った書き出しです");
        }

        var frameSize = _audio->frame_size;
        var offset = 0;
        while (offset < stereo.Length)
        {
            var count = Math.Min(stereo.Length - offset, (frameSize - _audioPendingFrames) * 2);
            stereo.Slice(offset, count).CopyTo(_audioPending.AsSpan(_audioPendingFrames * 2));
            _audioPendingFrames += count / 2;
            offset += count;
            if (_audioPendingFrames == frameSize)
            {
                SendAudio(frameSize);
            }
        }
    }

    /// <summary>エンコーダーに残っている分を出し切って、ファイルを閉じる</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_format is null, this);
        if (_finished)
        {
            return;
        }

        if (_audio is not null)
        {
            if (_audioPendingFrames > 0)
            {
                // 最後の半端な分は、無音で埋めて 1 フレームにする
                _audioPending.AsSpan(_audioPendingFrames * 2).Clear();
                SendAudio(_audio->frame_size);
            }

            Encode(_audio, _audioStream, null);
        }

        if (_video is not null)
        {
            Encode(_video, _videoStream, null);
        }

        ffmpeg.av_write_trailer(_format).ThrowIfError("ファイルの仕上げ");
        _finished = true;
    }

    public void Dispose()
    {
        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        foreach (var frame in new[] { _videoFrame, _audioFrame })
        {
            if (frame is not null)
            {
                var pointer = frame;
                ffmpeg.av_frame_free(&pointer);
            }
        }

        _videoFrame = _audioFrame = null;
        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        foreach (var context in new[] { _video, _audio })
        {
            if (context is not null)
            {
                var pointer = context;
                ffmpeg.avcodec_free_context(&pointer);
            }
        }

        _video = _audio = null;
        if (_format is not null)
        {
            if ((_format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _format->pb is not null)
            {
                ffmpeg.avio_closep(&_format->pb);
            }

            ffmpeg.avformat_free_context(_format);
            _format = null;
        }
    }

    // エンコーダーの候補（使いたい順）
    private static IEnumerable<string> Candidates(VideoCodecKind codec, EncoderPreference preference)
    {
        var prefix = codec == VideoCodecKind.H264 ? "h264" : "hevc";
        if (preference == EncoderPreference.Auto)
        {
            if (OperatingSystem.IsWindows())
            {
                yield return $"{prefix}_qsv";
                yield return $"{prefix}_nvenc";
                yield return $"{prefix}_amf";
                yield return $"{prefix}_mf";
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return $"{prefix}_videotoolbox";
            }
            else
            {
                yield return $"{prefix}_nvenc";
            }
        }

        yield return codec == VideoCodecKind.H264 ? "libopenh264" : "libkvazaar";
    }

    private void Open(VideoEncoderSettings? video, AudioEncoderSettings? audio)
    {
        AVFormatContext* format = null;
        ffmpeg.avformat_alloc_output_context2(&format, null, "mp4", _path).ThrowIfError("MP4 の準備");
        _format = format;
        var globalHeader = (_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;
        _packet = ffmpeg.av_packet_alloc();

        if (video is not null)
        {
            var tried = new List<string>();
            foreach (var name in Candidates(video.Codec, video.Preference))
            {
                tried.Add(name);
                var context = TryOpenVideo(name, video, globalHeader);
                if (context is not null)
                {
                    _video = context;
                    VideoEncoder = name;
                    break;
                }
            }

            if (_video is null)
            {
                // libkvazaar は幅と高さが 8 の倍数でないと開けない
                var hint = tried.Contains("libkvazaar") && (video.Width % 8 != 0 || video.Height % 8 != 0) ? "。ソフトウェアの HEVC は、幅と高さが 8 の倍数のときだけ使えます" : "";
                throw new MediaException($"{(video.Codec == VideoCodecKind.H264 ? "H.264" : "HEVC")} のエンコーダーを使えません（試したもの: {string.Join(", ", tried)}）{hint}");
            }

            _videoStream = ffmpeg.avformat_new_stream(_format, null);
            ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _video).ThrowIfError("映像の設定");
            _videoStream->time_base = _video->time_base;
            _videoStream->avg_frame_rate = _video->framerate;

            _videoFrame = ffmpeg.av_frame_alloc();
            _videoFrame->format = (int)_video->pix_fmt;
            _videoFrame->width = _video->width;
            _videoFrame->height = _video->height;
            _videoFrame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            _videoFrame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            _videoFrame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            _videoFrame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            ffmpeg.av_frame_get_buffer(_videoFrame, 0).ThrowIfError("映像のフレームの準備");

            // BGRA → YUV（BT.709・リミテッドレンジ）
            _scaler = ffmpeg.sws_getContext(
                video.Width, video.Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                video.Width, video.Height, _video->pix_fmt,
                (int)(SwsFlags.SWS_BILINEAR | SwsFlags.SWS_ACCURATE_RND | SwsFlags.SWS_FULL_CHR_H_INT), null, null, null);
            var coefficients = *(int4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            ffmpeg.sws_setColorspaceDetails(_scaler, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16);
        }

        if (audio is not null)
        {
            OpenAudio(audio, globalHeader);
        }

        if ((_format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            ffmpeg.avio_open(&_format->pb, _path, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError($"ファイルの作成（{_path}）");
        }

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
        try
        {
            ffmpeg.avformat_write_header(_format, &options).ThrowIfError("MP4 の書き始め");
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    // エンコーダーを開いてみる。開けなければ null
    private static AVCodecContext* TryOpenVideo(string name, VideoEncoderSettings settings, bool globalHeader)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec is null)
        {
            return null;
        }

        var context = ffmpeg.avcodec_alloc_context3(codec);
        context->width = settings.Width;
        context->height = settings.Height;
        context->time_base = new AVRational { num = (int)settings.FrameRate.Denominator, den = (int)settings.FrameRate.Numerator };
        context->framerate = new AVRational { num = (int)settings.FrameRate.Numerator, den = (int)settings.FrameRate.Denominator };
        context->pix_fmt = PixelFormat(context, codec);
        context->bit_rate = settings.BitrateKbps * 1000L;
        context->rc_max_rate = context->bit_rate * 2;
        context->rc_buffer_size = (int)Math.Min(int.MaxValue, context->bit_rate * 2);
        context->gop_size = (int)Math.Max(1, Math.Round(settings.FrameRate.ToDouble() * 2));
        context->thread_count = 0;
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        if (globalHeader)
        {
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        if (ffmpeg.avcodec_open2(context, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            return null;
        }

        return context;
    }

    // エンコーダーが受け付ける形式のうち、NV12 があればそれ（ハードウェアエンコーダー向け）、なければ YUV420P
    private static AVPixelFormat PixelFormat(AVCodecContext* context, AVCodec* codec)
    {
        void* configs = null;
        var count = 0;
        if (ffmpeg.avcodec_get_supported_config(context, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) >= 0 && configs is not null)
        {
            var formats = new ReadOnlySpan<AVPixelFormat>(configs, count);
            if (formats.Contains(AVPixelFormat.AV_PIX_FMT_NV12))
            {
                return AVPixelFormat.AV_PIX_FMT_NV12;
            }
        }

        return AVPixelFormat.AV_PIX_FMT_YUV420P;
    }

    private void OpenAudio(AudioEncoderSettings settings, bool globalHeader)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec is null)
        {
            throw new MediaException("AAC のエンコーダーを使えません");
        }

        _audio = ffmpeg.avcodec_alloc_context3(codec);
        _audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audio->sample_rate = settings.SampleRate;
        _audio->bit_rate = settings.BitrateKbps * 1000L;
        _audio->time_base = new AVRational { num = 1, den = settings.SampleRate };
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, 2);
        _audio->ch_layout = layout;
        if (globalHeader)
        {
            _audio->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        ffmpeg.avcodec_open2(_audio, codec, null).ThrowIfError("AAC のエンコーダーの準備");
        _audioStream = ffmpeg.avformat_new_stream(_format, null);
        ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audio).ThrowIfError("音声の設定");
        _audioStream->time_base = _audio->time_base;

        _audioFrame = ffmpeg.av_frame_alloc();
        _audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioFrame->nb_samples = _audio->frame_size;
        _audioFrame->sample_rate = settings.SampleRate;
        ffmpeg.av_channel_layout_copy(&_audioFrame->ch_layout, &_audio->ch_layout).ThrowIfError("音声のフレームの準備");
        ffmpeg.av_frame_get_buffer(_audioFrame, 0).ThrowIfError("音声のフレームの準備");
        _audioPending = new float[_audio->frame_size * 2];
    }

    // たまった音声を、左右に分けて（planar）エンコーダーへ送る
    private void SendAudio(int frames)
    {
        ffmpeg.av_frame_make_writable(_audioFrame).ThrowIfError("音声のフレームの準備");
        var left = (float*)_audioFrame->data[0];
        var right = (float*)_audioFrame->data[1];
        for (var i = 0; i < frames; i++)
        {
            left[i] = _audioPending[i * 2];
            right[i] = _audioPending[i * 2 + 1];
        }

        _audioFrame->nb_samples = frames;
        _audioFrame->pts = _audioPts;
        _audioPts += frames;
        _audioPendingFrames = 0;
        Encode(_audio, _audioStream, _audioFrame);
    }

    // フレームを送り（null なら出し切り）、出てきたパケットをファイルに書く
    private void Encode(AVCodecContext* context, AVStream* stream, AVFrame* frame)
    {
        ffmpeg.avcodec_send_frame(context, frame).ThrowIfError("エンコード");
        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(context, _packet);
            if (received == FfmpegErrors.TryAgain || received == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            received.ThrowIfError("エンコード");
            ffmpeg.av_packet_rescale_ts(_packet, context->time_base, stream->time_base);
            _packet->stream_index = stream->index;
            ffmpeg.av_interleaved_write_frame(_format, _packet).ThrowIfError("書き込み");
        }
    }
}
