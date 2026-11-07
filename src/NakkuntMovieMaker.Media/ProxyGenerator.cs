using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// プロキシの設定。大きさは素材の 1/Divisor で、短い辺が MaxShortSide、長い辺が MaxLongSide を超えないように縮める（既定は 1/2・最大 1080p）。
/// KeyframeSeconds はキーフレームの間隔（秒）。Encoder はエンコーダーの選び方（既定はハードウェアを先に試す）
/// </summary>
public sealed record ProxySettings(
    int Divisor = 2,
    int MaxShortSide = 1080,
    int MaxLongSide = 1920,
    double KeyframeSeconds = 0.5,
    EncoderPreference Encoder = EncoderPreference.Auto)
{
    public static ProxySettings Default { get; } = new();

    // キャッシュの名前に含める（設定を変えると別のプロキシになる）
    internal string CacheKey => FormattableString.Invariant($"{Divisor}|{MaxShortSide}|{MaxLongSide}|{KeyframeSeconds}|{Encoder}");

    /// <summary>
    /// 素材の大きさから、プロキシの大きさの上限を決める（縦横比を保ち、拡大はしない）。
    /// 実際の大きさは、デコーダーの丸めの後で偶数に切り下げるので、これより 1〜2 画素小さくなることがある
    /// </summary>
    public (int Width, int Height) TargetSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var longSide = Math.Max(width, height);
        var shortSide = Math.Min(width, height);
        var scale = Math.Min(1.0 / Math.Max(1, Divisor), Math.Min((double)MaxLongSide / longSide, (double)MaxShortSide / shortSide));
        scale = Math.Min(scale, 1);
        return (Even(width * scale), Even(height * scale));
    }

    private static int Even(double size) => Math.Max(2, (int)Math.Floor(size + 1e-9) & ~1);
}

/// <summary>作ったプロキシの情報</summary>
public sealed record ProxyInfo(int Width, int Height, int Frames, string Encoder, VideoColorTags Color);

/// <summary>
/// 動画の素材から、プレビュー用の軽いプロキシ（MP4・H.264・映像だけ）を作る。
/// 縮小し、キーフレームの間隔を短くし、B フレームを使わないので、シークと逆再生が速い。
/// フレームの表示時刻は素材のタイムスタンプをそのまま写す（可変フレームレートの素材でも、時刻 t で素材と同じフレームになる）。
/// 色のタグ（原色・伝達特性・変換式・範囲）も素材と同じにする。素材に変換式のタグがなければ、
/// デコーダーが使う変換式（HD 以上なら BT.709、それ未満なら BT.601）をタグとして付ける（縮小で判断が変わらないように）。
/// 回転（スマートフォンの縦撮りなど）は写さない。プロキシを使う側は、大きさ・回転などを元の素材の情報で扱うこと
/// </summary>
public static unsafe class ProxyGenerator
{
    // 1 画素・1 フレームあたりのビット数。プレビューで見て粗が目立たない程度
    private const double BitsPerPixel = 0.15;

    /// <summary>
    /// プロキシを outputPath に作る。progress には 0〜1 の進み具合を渡す。
    /// 失敗したとき・キャンセルしたときは、作りかけのファイルを消す
    /// </summary>
    /// <exception cref="MediaException">素材を開けない、映像がない、または使えるエンコーダーがない</exception>
    /// <exception cref="OperationCanceledException">キャンセルした</exception>
    public static ProxyInfo Generate(
        string sourcePath,
        string outputPath,
        ProxySettings? settings = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        settings ??= ProxySettings.Default;
        cancellationToken.ThrowIfCancellationRequested();
        FfmpegLibrary.Load();

        var source = SourceVideo.Read(sourcePath);
        var (maxWidth, maxHeight) = settings.TargetSize(source.Width, source.Height);
        using var decoder = VideoDecoder.Open(sourcePath, new VideoDecoderOptions(maxWidth, maxHeight, HardwareDecoding.Auto, CacheCapacity: 2));
        var width = decoder.OutputWidth & ~1;
        var height = decoder.OutputHeight & ~1;
        if (width < 2 || height < 2)
        {
            throw new MediaException($"映像が小さすぎてプロキシを作れません（{decoder.OutputWidth}×{decoder.OutputHeight}）: {sourcePath}");
        }

        var frameRate = source.FrameRate;
        var video = new VideoEncoderSettings(
            VideoCodecKind.H264,
            width,
            height,
            frameRate,
            Bitrate(width, height, frameRate),
            settings.Encoder,
            source.Color,
            KeyframeInterval: Math.Max(1, (int)Math.Round(frameRate.ToDouble() * settings.KeyframeSeconds)),
            MaxBFrames: 0,
            TimeBase: source.TimeBase);

        var frames = 0;
        string encoder;
        try
        {
            using (var writer = MediaWriter.Create(outputPath, video, null))
            {
                encoder = writer.VideoEncoder ?? "";
                progress?.Report(0);
                var frame = decoder.GetFrame(Rational.Zero);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 最初のフレームは 0 秒に置く。素材でも、最初のフレームより前の時刻は最初のフレームになるので、時刻の対応は変わらない
                    writer.WriteVideo(Fit(frame.Image, width, height), frames == 0 ? Rational.Zero : frame.Time);
                    frames++;
                    if (source.Duration is { } duration && duration > Rational.Zero)
                    {
                        progress?.Report(Math.Clamp(frame.Time.ToDouble() / duration.ToDouble(), 0, 1));
                    }

                    // 長さが分からないフレーム（最初のフレームより前を求めたときなど）は、もう一度求めて確かめる。それでも分からなければ最後のフレーム
                    var length = frame.Duration ?? decoder.GetFrame(frame.Time).Duration;
                    if (length is not { } step)
                    {
                        break;
                    }

                    var next = decoder.GetFrame(frame.Time + step);
                    if (next.Time <= frame.Time)
                    {
                        break;
                    }

                    frame = next;
                }

                cancellationToken.ThrowIfCancellationRequested();
                writer.Finish();
            }
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }

        progress?.Report(1);
        return new ProxyInfo(width, height, frames, encoder, source.Color);
    }

    // ビットレート（kbps）。小さすぎる・大きすぎる値は切り詰める
    private static int Bitrate(int width, int height, Rational frameRate)
    {
        var kbps = width * (double)height * Math.Clamp(frameRate.ToDouble(), 1, 240) * BitsPerPixel / 1000;
        return (int)Math.Clamp(kbps, 500, 40_000);
    }

    // デコーダーの出した画像が奇数の大きさなら、右端・下端の 1 画素を落とす
    private static BgraImage Fit(BgraImage image, int width, int height)
    {
        if (image.Width == width && image.Height == height)
        {
            return image;
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            image.Pixels.AsSpan(y * image.Stride, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }

        return new BgraImage(width, height, pixels);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // 素材の映像の情報のうち、プロキシに写すもの
    private sealed record SourceVideo(int Width, int Height, Rational TimeBase, Rational FrameRate, Rational? Duration, VideoColorTags Color)
    {
        public static SourceVideo Read(string path)
        {
            AVFormatContext* format = null;
            var opened = ffmpeg.avformat_open_input(&format, path, null, null);
            if (opened < 0)
            {
                throw new MediaException($"メディアファイルとして開けません（{FfmpegErrors.Describe(opened)}）: {path}");
            }

            try
            {
                ffmpeg.avformat_find_stream_info(format, null).ThrowIfError("ストリームの情報の取得");
                var index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (index < 0)
                {
                    throw new MediaException($"映像が含まれていません: {path}");
                }

                var stream = format->streams[index];
                var parameters = stream->codecpar;
                if (stream->time_base.num <= 0 || stream->time_base.den <= 0)
                {
                    throw new MediaException($"映像のタイムベースが正しくありません: {path}");
                }

                var timeBase = new Rational(stream->time_base.num, stream->time_base.den);
                var frameRate = stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0
                    ? new Rational(stream->avg_frame_rate.num, stream->avg_frame_rate.den)
                    : stream->r_frame_rate.num > 0 && stream->r_frame_rate.den > 0
                        ? new Rational(stream->r_frame_rate.num, stream->r_frame_rate.den)
                        : new Rational(30, 1);
                Rational? duration = stream->duration > 0
                    ? stream->duration * timeBase
                    : format->duration > 0 ? new Rational(format->duration, ffmpeg.AV_TIME_BASE) : null;
                return new SourceVideo(parameters->width, parameters->height, timeBase, frameRate, duration, ColorTags(parameters));
            }
            finally
            {
                ffmpeg.avformat_close_input(&format);
            }
        }

        // 素材の色のタグ。変換式と範囲は、VideoDecoder が RGB にするときに使うものと同じにする
        private static VideoColorTags ColorTags(AVCodecParameters* parameters)
        {
            var pixelFormat = (AVPixelFormat)parameters->format;
            var descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
            var isRgb = descriptor is not null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
            var matrix = parameters->color_space switch
            {
                _ when isRgb => AVColorSpace.AVCOL_SPC_BT709,
                AVColorSpace.AVCOL_SPC_BT709 or AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M
                    or AVColorSpace.AVCOL_SPC_SMPTE240M or AVColorSpace.AVCOL_SPC_FCC
                    or AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => parameters->color_space,
                _ => parameters->height >= 720 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_SMPTE170M,
            };

            var formatName = ffmpeg.av_get_pix_fmt_name(pixelFormat) ?? "";
            var fullRange = !isRgb && (parameters->color_range == AVColorRange.AVCOL_RANGE_JPEG || formatName.StartsWith("yuvj", StringComparison.Ordinal));
            return new VideoColorTags((int)parameters->color_primaries, (int)parameters->color_trc, (int)matrix, fullRange);
        }
    }
}
