using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// 動画・画像からサムネイルを取り出す。タイムラインに並べる複数の時刻のサムネイルは、
/// 1 回開いたファイルを使い回して取り出す
/// </summary>
public static unsafe class ThumbnailExtractor
{
    /// <summary>指定した時刻（秒）のフレームを、幅 maxWidth・高さ maxHeight に収まる大きさで取り出す</summary>
    /// <exception cref="MediaException">ファイルを開けない、または映像が含まれていない</exception>
    public static BgraImage Extract(string path, Rational time, int maxWidth, int maxHeight) =>
        ExtractMany(path, [time], maxWidth, maxHeight)[0];

    /// <summary>複数の時刻のフレームを取り出す。時刻が素材の長さを超えるときは、最後のフレームを返す</summary>
    public static IReadOnlyList<BgraImage> ExtractMany(string path, IReadOnlyList<Rational> times, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        if (!File.Exists(path))
        {
            throw new MediaException($"ファイルが見つかりません: {path}");
        }

        FfmpegLibrary.Load();
        using var decoder = new FrameDecoder(path);
        var images = new List<BgraImage>(times.Count);
        foreach (var time in times)
        {
            images.Add(decoder.DecodeAt(time, maxWidth, maxHeight));
        }

        return images;
    }

    /// <summary>1 つの映像ストリームをデコードする</summary>
    private sealed class FrameDecoder : IDisposable
    {
        private AVFormatContext* _format;
        private AVCodecContext* _codec;
        private AVPacket* _packet;
        private AVFrame* _frame;
        private SwsContext* _scaler;
        private readonly int _streamIndex;
        private readonly AVRational _timeBase;

        public FrameDecoder(string path)
        {
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
                _timeBase = stream->time_base;
                _codec = ffmpeg.avcodec_alloc_context3(decoder);
                ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar).ThrowIfError("デコーダーの設定");
                _codec->thread_count = 0; // CPU のコア数に合わせて自動で決める
                ffmpeg.avcodec_open2(_codec, decoder, null).ThrowIfError("デコーダーの準備");

                _packet = ffmpeg.av_packet_alloc();
                _frame = ffmpeg.av_frame_alloc();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public BgraImage DecodeAt(Rational time, int maxWidth, int maxHeight)
        {
            var target = time <= Rational.Zero || _timeBase.den == 0
                ? 0
                : Rational.Rescale(1, time, new Rational(_timeBase.num, _timeBase.den), Rounding.Down);

            // 目的の時刻より前のキーフレームに戻ってから、目的の時刻までデコードする
            ffmpeg.av_seek_frame(_format, _streamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_codec);

            BgraImage? last = null;
            while (true)
            {
                var read = ffmpeg.av_read_frame(_format, _packet);
                if (read < 0)
                {
                    // ファイルの終わり。デコーダーに残っているフレームを出し切る
                    ffmpeg.avcodec_send_packet(_codec, null);
                }
                else if (_packet->stream_index != _streamIndex)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }
                else
                {
                    var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
                    ffmpeg.av_packet_unref(_packet);
                    if (sent < 0 && sent != FfmpegErrors.TryAgain)
                    {
                        sent.ThrowIfError("映像のデコード");
                    }
                }

                while (true)
                {
                    var received = ffmpeg.avcodec_receive_frame(_codec, _frame);
                    if (received == FfmpegErrors.TryAgain)
                    {
                        break;
                    }

                    if (received == ffmpeg.AVERROR_EOF)
                    {
                        return last ?? throw new MediaException("フレームを取り出せませんでした");
                    }

                    received.ThrowIfError("映像のデコード");
                    var pts = _frame->best_effort_timestamp;
                    var reached = pts == ffmpeg.AV_NOPTS_VALUE || pts >= target;
                    last = reached || last is null ? Convert(maxWidth, maxHeight) : last;
                    ffmpeg.av_frame_unref(_frame);
                    if (reached)
                    {
                        return last;
                    }
                }

                if (read < 0)
                {
                    return last ?? throw new MediaException("フレームを取り出せませんでした");
                }
            }
        }

        // デコードしたフレームを、縦横比を保って枠に収まる大きさの BGRA にする
        private BgraImage Convert(int maxWidth, int maxHeight)
        {
            var sourceWidth = _frame->width;
            var sourceHeight = _frame->height;
            var scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
            scale = Math.Min(scale, 1);
            var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            _scaler = ffmpeg.sws_getCachedContext(
                _scaler,
                sourceWidth, sourceHeight, (AVPixelFormat)_frame->format,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_AREA, null, null, null);
            if (_scaler is null)
            {
                throw new MediaException("画像の縮小の準備に失敗しました");
            }

            var pixels = new byte[width * height * 4];
            fixed (byte* destination = pixels)
            {
                ffmpeg.sws_scale(
                    _scaler,
                    _frame->data.ToArray(), _frame->linesize.ToArray(), 0, sourceHeight,
                    [destination], [width * 4]).ThrowIfError("画像の縮小");
            }

            PremultiplyAlpha(pixels);
            return new BgraImage(width, height, pixels);
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

        public void Dispose()
        {
            if (_scaler is not null)
            {
                ffmpeg.sws_freeContext(_scaler);
                _scaler = null;
            }

            if (_frame is not null)
            {
                var frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_packet is not null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_codec is not null)
            {
                var codec = _codec;
                ffmpeg.avcodec_free_context(&codec);
                _codec = null;
            }

            if (_format is not null)
            {
                var format = _format;
                ffmpeg.avformat_close_input(&format);
                _format = null;
            }
        }
    }
}
