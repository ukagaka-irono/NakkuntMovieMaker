using System.Collections.Immutable;
using System.Diagnostics;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Tachie;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Engine.Export;

/// <summary>書き出しのプリセット。大きさが null ならシーンの大きさ</summary>
public sealed record ExportPreset(string Id, string Name, VideoCodecKind Codec, int? Width, int? Height, int VideoBitrateKbps, int AudioBitrateKbps = 192);

/// <summary>組み込みのプリセット</summary>
public static class ExportPresets
{
    public static readonly ExportPreset YouTube1080p = new("youtube-1080p", "YouTube 1080p（H.264）", VideoCodecKind.H264, 1920, 1080, 12000);

    public static readonly ExportPreset YouTube1080pHevc = new("youtube-1080p-hevc", "YouTube 1080p（HEVC・小さいファイル）", VideoCodecKind.Hevc, 1920, 1080, 8000);

    public static readonly ExportPreset Vertical = new("vertical-1080x1920", "縦長動画 1080×1920（ショート動画）", VideoCodecKind.H264, 1080, 1920, 10000);

    public static readonly ExportPreset Light720p = new("light-720p", "軽い 720p（H.264）", VideoCodecKind.H264, 1280, 720, 5000, 128);

    public static readonly ExportPreset SceneSize = new("scene", "シーンの大きさのまま（H.264）", VideoCodecKind.H264, null, null, 12000);

    public static ImmutableArray<ExportPreset> All { get; } = [YouTube1080p, YouTube1080pHevc, Vertical, Light720p, SceneSize];
}

/// <summary>
/// 動画の書き出しの設定。Width・Height は出力の大きさ（null ならシーンの大きさ。縦横比が違えば、黒い帯を付けて中央に置く）、
/// From・To は書き出す範囲（フレーム、[From, To)。null ならシーン全体）、LoudnessTarget を指定すると、音全体のラウドネスを
/// その値（LUFS）にそろえる（山は −1dBFS まで）。Workers は並列に描くスレッドの数（0 なら自動）。
/// ExternalFfmpeg を指定すると、同梱の FFmpeg ではなく、外部の ffmpeg（利用者が自分で入手したもの。x264・x265）で圧縮する（Encoder は使わない）
/// </summary>
public sealed record ExportOptions(
    string Path,
    VideoCodecKind Codec = VideoCodecKind.H264,
    int? Width = null,
    int? Height = null,
    int VideoBitrateKbps = 12000,
    int AudioBitrateKbps = 192,
    long? From = null,
    long? To = null,
    EncoderPreference Encoder = EncoderPreference.Auto,
    double? LoudnessTarget = null,
    int Workers = 0,
    ExternalFfmpegInfo? ExternalFfmpeg = null)
{
    public static ExportOptions FromPreset(string path, ExportPreset preset) =>
        new(path, preset.Codec, preset.Width, preset.Height, preset.VideoBitrateKbps, preset.AudioBitrateKbps);
}

/// <summary>書き出しの進み具合</summary>
public sealed record ExportProgress(long Frame, long TotalFrames, TimeSpan Elapsed)
{
    public double Fraction => TotalFrames == 0 ? 1 : (double)Frame / TotalFrames;
}

/// <summary>書き出しの結果。VideoEncoder は使った映像のエンコーダー</summary>
public sealed record ExportResult(string Path, long Frames, string? VideoEncoder, TimeSpan Elapsed);

/// <summary>
/// シーンを動画（MP4）・音声（WAV）・静止画に書き出す。描画はプレビューと同じ処理で、動画の変換は質を優先する。
/// 別々のフレームを並列に描き、順番どおりにエンコーダーへ渡す。内容が前のフレームと同じフレーム（止まっている区間）は、描き直さずに使い回す。
/// 書き出しの途中で止めた・失敗したときは、作りかけのファイルを消す
/// </summary>
public sealed class ExportPipeline : IDisposable
{
    private readonly Scene _scene;
    private readonly ProjectMediaSource _media;
    private readonly TimelineContext _timeline;
    private readonly SkiaCpuBackend _backend = new();

    public ExportPipeline(Project project, SceneId scene, string projectFolder)
    {
        _scene = project.GetScene(scene);
        _media = new ProjectMediaSource(project, projectFolder, new MediaCache(Path.Combine(projectFolder, ".nmm", "cache")), ConversionQuality.Accurate);
        _timeline = new TimelineContext(project.Characters, new TachieLoader(projectFolder), new ProjectLipSync(_media));
    }

    /// <summary>動画を書き出す</summary>
    /// <exception cref="MediaException">ファイルを作れない、または使えるエンコーダーがない</exception>
    /// <exception cref="ArgumentException">範囲が空</exception>
    public Task<ExportResult> ExportVideoAsync(ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExportVideo(options, progress, cancellationToken), cancellationToken);

    /// <summary>範囲の音を WAV に書き出す（loudnessTarget を指定すると、ラウドネスをそろえる）</summary>
    public Task<ExportResult> ExportWavAsync(
        string path, long? from = null, long? to = null, double? loudnessTarget = null, WavSampleFormat format = WavSampleFormat.Pcm16, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var (start, end) = Range(from, to);
            var samples = MixAudio(start, end, loudnessTarget, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            WavWriter.WriteFile(path, samples, 2, _scene.Format.SampleRate, format);
            return new ExportResult(path, end - start, null, watch.Elapsed);
        }, cancellationToken);

    /// <summary>1 フレームを静止画（PNG・JPEG・WebP。拡張子で決める）に書き出す</summary>
    public Task ExportStillAsync(string path, long frame, int? width = null, int? height = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImageFiles.Save(RenderFrame(frame, OutputSize(width, height, even: false), cancellationToken), path), cancellationToken);

    public void Dispose() => _media.Dispose();

    private ExportResult ExportVideo(ExportOptions options, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var (start, end) = Range(options.From, options.To);
        var size = OutputSize(options.Width, options.Height, even: true);
        var format = _scene.Format;
        var audio = MixAudio(start, end, options.LoudnessTarget, cancellationToken);
        var firstSample = TimeMath.FrameToSample(start, format.Fps, format.SampleRate);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Path))!);
        var videoSettings = new VideoEncoderSettings(options.Codec, size.Width, size.Height, format.Fps, options.VideoBitrateKbps, options.Encoder);
        var audioSettings = new AudioEncoderSettings(format.SampleRate, options.AudioBitrateKbps);
        IMediaFileWriter writer = options.ExternalFfmpeg is { } ffmpeg
            ? ExternalFfmpegWriter.Create(ffmpeg, options.Path, videoSettings, audioSettings)
            : MediaWriter.Create(options.Path, videoSettings, audioSettings);
        var completed = false;
        try
        {
            var workers = options.Workers > 0 ? options.Workers : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            var pending = new Queue<(long Frame, Task<BgraImage> Image)>();
            FramePlan? previousPlan = null;
            Task<BgraImage>? previousImage = null;
            var next = start;
            var writtenSamples = 0L;
            for (var frame = start; frame < end; frame++)
            {
                // 少し先のフレームまで、並列に描き始めておく
                while (next < end && pending.Count < workers * 2)
                {
                    var plan = TimelineEvaluator.Evaluate(_scene, next, timeline: _timeline);
                    var image = plan == previousPlan && previousImage is not null
                        ? previousImage
                        : Task.Run(() => Fit(_backend.Render(plan, _media, size.Scale, cancellationToken), size), cancellationToken);
                    pending.Enqueue((next, image));
                    previousPlan = plan;
                    previousImage = image;
                    next++;
                }

                var (_, rendered) = pending.Dequeue();
                writer.WriteVideo(rendered.GetAwaiter().GetResult());

                // そのフレームの終わりまでの音を書く
                var sampleEnd = TimeMath.FrameToSample(frame + 1, format.Fps, format.SampleRate) - firstSample;
                writer.WriteAudio(audio.AsSpan((int)(writtenSamples * 2), (int)((sampleEnd - writtenSamples) * 2)));
                writtenSamples = sampleEnd;
                progress?.Report(new ExportProgress(frame - start + 1, end - start, watch.Elapsed));
                cancellationToken.ThrowIfCancellationRequested();
            }

            writer.Finish();
            completed = true;
            return new ExportResult(options.Path, end - start, writer.VideoEncoder, watch.Elapsed);
        }
        finally
        {
            writer.Dispose();
            if (!completed)
            {
                File.Delete(options.Path);
            }
        }
    }

    // 書き出す範囲（フレーム）。指定がなければシーン全体
    private (long Start, long End) Range(long? from, long? to)
    {
        var start = Math.Max(0, from ?? 0);
        var end = to ?? _scene.Duration;
        if (end <= start)
        {
            throw new ArgumentException($"書き出す範囲が空です（{start}〜{end} フレーム）");
        }

        return (start, end);
    }

    // 範囲の音（ステレオ）。ラウドネスを指定されていれば、山を −1dBFS までに抑えてそろえる
    private float[] MixAudio(long start, long end, double? loudnessTarget, CancellationToken cancellationToken)
    {
        var format = _scene.Format;
        var plan = AudioEvaluator.Build(_scene);
        var first = TimeMath.FrameToSample(start, format.Fps, format.SampleRate);
        var last = TimeMath.FrameToSample(end, format.Fps, format.SampleRate);
        var samples = AudioMixdown.Render(plan, _media, first, last, cancellationToken);
        if (loudnessTarget is { } target)
        {
            Loudness.Normalize(samples, format.SampleRate, target, peakCeilingDb: -1);
        }

        return samples;
    }

    private BgraImage RenderFrame(long frame, Output size, CancellationToken cancellationToken) =>
        Fit(_backend.Render(TimelineEvaluator.Evaluate(_scene, frame, timeline: _timeline), _media, size.Scale, cancellationToken), size);

    // 出力の大きさと、シーンを描く倍率（縦横比が違えば、収まる倍率で描いて黒い帯を付ける）
    private Output OutputSize(int? width, int? height, bool even)
    {
        var format = _scene.Format;
        var outputWidth = width ?? format.Width;
        var outputHeight = height ?? format.Height;
        if (even)
        {
            outputWidth += outputWidth % 2;
            outputHeight += outputHeight % 2;
        }

        var scale = Math.Min((double)outputWidth / format.Width, (double)outputHeight / format.Height);
        return new Output(outputWidth, outputHeight, scale);
    }

    // 描いた画像を出力の大きさの中央に置く（大きさが同じなら、そのまま）
    private static BgraImage Fit(BgraImage image, Output size)
    {
        if (image.Width == size.Width && image.Height == size.Height)
        {
            return image;
        }

        var pixels = new byte[size.Width * size.Height * 4];
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var left = (size.Width - image.Width) / 2;
        var top = (size.Height - image.Height) / 2;
        var width = Math.Min(image.Width, size.Width);
        for (var y = 0; y < Math.Min(image.Height, size.Height); y++)
        {
            Buffer.BlockCopy(image.Pixels, y * image.Stride, pixels, ((y + Math.Max(0, top)) * size.Width + Math.Max(0, left)) * 4, width * 4);
        }

        return new BgraImage(size.Width, size.Height, pixels);
    }

    private sealed record Output(int Width, int Height, double Scale);
}
