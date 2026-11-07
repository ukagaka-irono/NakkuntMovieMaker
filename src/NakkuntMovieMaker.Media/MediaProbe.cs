using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>メディアファイルの情報。Duration は秒（分からなければ null）</summary>
public sealed record MediaInfo(
    string Path,
    string FormatName,
    AssetKind Kind,
    Rational? Duration,
    ImmutableArray<VideoStreamInfo> VideoStreams,
    ImmutableArray<AudioStreamInfo> AudioStreams)
{
    /// <summary>主に使う映像（最初の映像ストリーム）</summary>
    public VideoStreamInfo? Video => VideoStreams.IsEmpty ? null : VideoStreams[0];

    /// <summary>主に使う音声（最初の音声ストリーム）</summary>
    public AudioStreamInfo? Audio => AudioStreams.IsEmpty ? null : AudioStreams[0];
}

/// <summary>
/// 映像ストリームの情報。FrameRate は平均のフレームレート（分からなければ null）、
/// Rotation は表示するときの回転（度、時計回り。スマートフォンで縦に撮った動画など）
/// </summary>
public sealed record VideoStreamInfo(
    int Index,
    string Codec,
    int Width,
    int Height,
    Rational? FrameRate,
    string PixelFormat,
    int Rotation,
    long? FrameCount,
    Rational? Duration)
{
    /// <summary>ストリームに付いている色の情報（タグがなければ推測した値）</summary>
    public VideoColorInfo Color { get; init; } = VideoColorInfo.Srgb;
}

/// <summary>音声ストリームの情報</summary>
public sealed record AudioStreamInfo(int Index, string Codec, int SampleRate, int Channels, Rational? Duration);

/// <summary>FFmpeg でメディアファイルの情報を調べる</summary>
public static unsafe class MediaProbe
{
    // 1 枚の画像として扱う形式（FFmpeg の画像用の読み込み）
    private static readonly ImmutableHashSet<string> ImageCodecs =
        ["png", "mjpeg", "bmp", "webp", "tiff", "targa", "gif", "jpegxl", "qoi"];

    /// <exception cref="MediaException">ファイルを開けない、またはメディアではない</exception>
    public static MediaInfo Probe(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new MediaException($"ファイルが見つかりません: {path}");
        }

        FfmpegLibrary.Load();

        AVFormatContext* format = null;
        try
        {
            var opened = ffmpeg.avformat_open_input(&format, path, null, null);
            if (opened < 0)
            {
                throw new MediaException($"メディアファイルとして開けません（{FfmpegErrors.Describe(opened)}）: {path}");
            }

            ffmpeg.avformat_find_stream_info(format, null).ThrowIfError("ストリームの情報の取得");

            var formatName = Marshal.PtrToStringUTF8((nint)format->iformat->name) ?? "";
            var videos = ImmutableArray.CreateBuilder<VideoStreamInfo>();
            var audios = ImmutableArray.CreateBuilder<AudioStreamInfo>();
            for (var i = 0; i < format->nb_streams; i++)
            {
                var stream = format->streams[i];
                var parameters = stream->codecpar;
                switch (parameters->codec_type)
                {
                    // 音楽ファイルのジャケット画像などは映像として扱わない
                    case AVMediaType.AVMEDIA_TYPE_VIDEO when (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0:
                        videos.Add(new VideoStreamInfo(
                            i,
                            ffmpeg.avcodec_get_name(parameters->codec_id),
                            parameters->width,
                            parameters->height,
                            ToRational(stream->avg_frame_rate) ?? ToRational(stream->r_frame_rate),
                            ffmpeg.av_get_pix_fmt_name((AVPixelFormat)parameters->format) ?? "unknown",
                            GetRotation(parameters),
                            stream->nb_frames > 0 ? stream->nb_frames : null,
                            StreamDuration(stream))
                        {
                            Color = FfmpegColor.FromParameters(parameters),
                        });
                        break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        audios.Add(new AudioStreamInfo(
                            i,
                            ffmpeg.avcodec_get_name(parameters->codec_id),
                            parameters->sample_rate,
                            parameters->ch_layout.nb_channels,
                            StreamDuration(stream)));
                        break;
                }
            }

            if (videos.Count == 0 && audios.Count == 0)
            {
                throw new MediaException($"映像も音声も含まれていません: {path}");
            }

            Rational? duration = format->duration > 0 ? new Rational(format->duration, ffmpeg.AV_TIME_BASE) : null;
            var kind = DecideKind(formatName, videos, audios.Count > 0, duration);
            if (kind == AssetKind.Image)
            {
                // 画像の読み込みが仮に付ける 25fps などは意味がないので消す
                var image = videos[0] with { FrameRate = null, FrameCount = null, Duration = null };
                return new MediaInfo(path, formatName, kind, null, [image], []);
            }

            return new MediaInfo(path, formatName, kind, duration, videos.ToImmutable(), audios.ToImmutable());
        }
        finally
        {
            if (format is not null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }
    }

    private static AssetKind DecideKind(string formatName, ImmutableArray<VideoStreamInfo>.Builder videos, bool hasAudio, Rational? duration)
    {
        if (videos.Count == 0)
        {
            return AssetKind.Audio;
        }

        // 画像用の読み込み（image2・png_pipe など）で、画像の形式・音声なし・1 フレームなら画像
        var video = videos[0];
        var imageFormat = formatName == "image2" || formatName.EndsWith("_pipe", StringComparison.Ordinal) || formatName == "gif";
        var singleFrame = video.FrameCount is null or <= 1
            && (duration is null || duration.Value <= video.FrameRate?.Reciprocal());
        return imageFormat && !hasAudio && ImageCodecs.Contains(video.Codec) && singleFrame
            ? AssetKind.Image
            : AssetKind.Video;
    }

    private static int GetRotation(AVCodecParameters* parameters)
    {
        var sideData = ffmpeg.av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sideData is null || sideData->size < (ulong)(9 * sizeof(int)))
        {
            return 0;
        }

        // av_display_rotation_get は反時計回りの角度を返すので、時計回りにする
        var angle = -ffmpeg.av_display_rotation_get(*(int9*)sideData->data);
        if (double.IsNaN(angle))
        {
            return 0;
        }

        return ((int)Math.Round(angle) % 360 + 360) % 360;
    }

    private static Rational? StreamDuration(AVStream* stream) =>
        stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE && stream->time_base.den != 0
            ? new Rational(stream->duration, 1) * new Rational(stream->time_base.num, stream->time_base.den)
            : null;

    private static Rational? ToRational(AVRational value) =>
        value.num > 0 && value.den > 0 ? new Rational(value.num, value.den) : null;
}
