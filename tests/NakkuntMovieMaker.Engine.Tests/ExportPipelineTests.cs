using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;
using SkiaSharp;

namespace NakkuntMovieMaker.Engine.Tests;

/// <summary>
/// 書き出し。160×90・30fps・2 秒のシーン（前半の 1 秒は赤、後半は青の画面。前半だけ 1kHz の音）を書き出し、
/// FFmpeg で読み直して確かめる
/// </summary>
public sealed class ExportPipelineTests : IDisposable
{
    private static readonly SceneFormat Small = new(160, 90, 30, 48000);
    private static readonly RgbaColor Red = new(255, 0, 0);
    private static readonly RgbaColor Blue = new(0, 0, 255);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
    private readonly Project _project;

    public ExportPipelineTests()
    {
        Directory.CreateDirectory(_folder);
        var project = Project.CreateNew("テスト", Small, 2);
        var asset = new Asset(AssetId.New(), AssetKind.Audio, Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-1s.wav"));
        var scene = project.Root;
        var red = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 0, 30, new ShapeStyle(ShapeKind.Rectangle, 160, 90, new SolidPaint(Red)));
        var blue = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 30, 30, new ShapeStyle(ShapeKind.Rectangle, 160, 90, new SolidPaint(Blue)));
        var tone = new AudioItem(ItemId.New(), scene.Layers[1].Id, 0, 30, asset.Id);
        scene = scene with { Items = scene.Items.Add(red.Id, red).Add(blue.Id, blue).Add(tone.Id, tone) };
        _project = project.WithScene(scene) with { Assets = project.Assets.Add(asset.Id, asset) };
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static void RequireFfmpeg()
    {
        var loaded = FfmpegLibrary.TryLoad(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") != "1", $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    private ExportPipeline CreatePipeline() => new(_project, _project.RootScene, _folder);

    private string Output(string name) => Path.Combine(_folder, "out", name);

    // 書き出した動画の、frame フレーム目の (x, y)（省略すると中央）の色
    private static (byte R, byte G, byte B) ColorAt(string path, long frame, int x = -1, int y = -1)
    {
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));
        var image = decoder.GetFrame(TimeMath.FrameToSeconds(frame, 30)).Image;
        var (b, g, r, _) = image.GetPixel(x < 0 ? image.Width / 2 : x, y < 0 ? image.Height / 2 : y);
        return (r, g, b);
    }

    private static void AssertRed((byte R, byte G, byte B) color) => Assert.True(color.R > 200 && color.G < 50 && color.B < 50, $"赤ではない: {color}");

    private static void AssertBlue((byte R, byte G, byte B) color) => Assert.True(color.B > 200 && color.R < 50 && color.G < 50, $"青ではない: {color}");

    private static void AssertBlack((byte R, byte G, byte B) color) => Assert.True(color.R < 30 && color.G < 30 && color.B < 30, $"黒ではない: {color}");

    // 映像の長さとフレーム数、音声の形式を確かめる
    private static void AssertStreams(string path, string codec, int width, int height, long frames)
    {
        var info = MediaProbe.Probe(path);
        var video = Assert.IsType<VideoStreamInfo>(info.Video);
        var audio = Assert.IsType<AudioStreamInfo>(info.Audio);
        Assert.Equal((codec, width, height, "yuv420p"), (video.Codec, video.Width, video.Height, video.PixelFormat));
        Assert.Equal(new Rational(30, 1), video.FrameRate);
        Assert.Equal(frames, video.FrameCount);
        Assert.InRange(info.Duration!.Value.ToDouble(), frames / 30.0 - 1 / 30.0, frames / 30.0 + 1 / 30.0);
        Assert.Equal(("aac", 48000, 2), (audio.Codec, audio.SampleRate, audio.Channels));
    }

    [Fact]
    public async Task ExportsTwoSecondsOfH264WithSoftwareEncoder()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("software.mp4");
        var reports = new List<ExportProgress>();

        var result = await pipeline.ExportVideoAsync(
            new ExportOptions(path, Encoder: EncoderPreference.Software, Workers: 2), new SynchronousProgress<ExportProgress>(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(("libopenh264", 60L), (result.VideoEncoder, result.Frames));
        AssertStreams(path, "h264", 160, 90, 60);
        AssertRed(ColorAt(path, 15));
        AssertRed(ColorAt(path, 29));
        AssertBlue(ColorAt(path, 30));
        AssertBlue(ColorAt(path, 59));
        Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), reports.Select(report => report.Frame));
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public async Task ExportsHevc()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("hevc.mp4");

        // ソフトウェアの HEVC（libkvazaar）は、幅と高さが 8 の倍数でないと使えない
        var error = await Assert.ThrowsAsync<MediaException>(() => pipeline.ExportVideoAsync(
            new ExportOptions(Output("odd.mp4"), VideoCodecKind.Hevc, Encoder: EncoderPreference.Software), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("8 の倍数", error.Message);
        Assert.False(File.Exists(Output("odd.mp4")));

        var result = await pipeline.ExportVideoAsync(
            new ExportOptions(path, VideoCodecKind.Hevc, Width: 128, Height: 72, Encoder: EncoderPreference.Software), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("libkvazaar", result.VideoEncoder);
        AssertStreams(path, "hevc", 128, 72, 60);
        AssertBlue(ColorAt(path, 45));
    }

    [Fact]
    public async Task ExportsWithTheBestAvailableEncoder()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("auto.mp4");

        // ハードウェアエンコーダーがあればそれを、なければ OpenH264 を使う
        var result = await pipeline.ExportVideoAsync(new ExportOptions(path), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MediaWriter.AvailableEncoders(VideoCodecKind.H264)[0], result.VideoEncoder);
        AssertStreams(path, "h264", 160, 90, 60);
        AssertRed(ColorAt(path, 15));
        AssertBlue(ColorAt(path, 45));
    }

    // PATH にある ffmpeg（x264・x265 を含むもの）。なければスキップする
    private static ExternalFfmpegInfo RequireExternalFfmpeg(VideoCodecKind codec)
    {
        ExternalFfmpegInfo? info = null;
        try
        {
            info = ExternalFfmpeg.Probe("ffmpeg");
        }
        catch (MediaException)
        {
        }

        Assert.SkipWhen(info is null || !info.Supports(codec), $"PATH に {ExternalFfmpegInfo.EncoderFor(codec)} を含む ffmpeg がないためスキップします");
        return info!;
    }

    [Theory]
    [InlineData(VideoCodecKind.H264, "h264", "libx264")]
    [InlineData(VideoCodecKind.Hevc, "hevc", "libx265")]
    public async Task ExportsWithAnExternalFfmpeg(VideoCodecKind codec, string codecName, string encoder)
    {
        RequireFfmpeg();
        var ffmpeg = RequireExternalFfmpeg(codec);
        using var pipeline = CreatePipeline();
        var path = Output($"external-{codecName}.mp4");

        var result = await pipeline.ExportVideoAsync(new ExportOptions(path, codec, ExternalFfmpeg: ffmpeg), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((encoder, 60L), (result.VideoEncoder, result.Frames));
        AssertStreams(path, codecName, 160, 90, 60);
        AssertRed(ColorAt(path, 15));
        AssertBlue(ColorAt(path, 45));

        // 一時ファイルは残さない
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));

        // 前半だけ鳴っている
        using var audio = new MediaCache(Path.Combine(_folder, "cache")).OpenAudio(path, TestContext.Current.CancellationToken);
        var samples = new float[audio.FrameCount * audio.Channels];
        audio.Read(0, samples);
        Assert.True(Rms(samples.AsSpan(0, 24000 * 2)) > 0.03, "前半が無音");
        Assert.True(Rms(samples.AsSpan(60000 * 2)) < 0.001, "後半に音がある");
    }

    [Fact]
    public async Task ReportsAnExternalFfmpegWithoutTheEncoder()
    {
        RequireFfmpeg();
        var ffmpeg = RequireExternalFfmpeg(VideoCodecKind.H264) with { Encoders = [] };
        using var pipeline = CreatePipeline();
        var path = Output("no-x264.mp4");

        var error = await Assert.ThrowsAsync<MediaException>(() => pipeline.ExportVideoAsync(
            new ExportOptions(path, ExternalFfmpeg: ffmpeg), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("libx264", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RejectsAProgramThatIsNotFfmpeg()
    {
        var notFfmpeg = Path.Combine(_folder, "missing", "ffmpeg.exe");

        var error = Assert.Throws<MediaException>(() => ExternalFfmpeg.Probe(notFfmpeg));

        Assert.Contains("実行できません", error.Message);
    }

    [Fact]
    public async Task ExportsOnlyTheRange()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("range.mp4");

        var result = await pipeline.ExportVideoAsync(
            new ExportOptions(path, From: 15, To: 45, Encoder: EncoderPreference.Software), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(30, result.Frames);
        AssertStreams(path, "h264", 160, 90, 30);
        AssertRed(ColorAt(path, 0));
        AssertRed(ColorAt(path, 14));
        AssertBlue(ColorAt(path, 15));
    }

    [Fact]
    public async Task FitsTheSceneIntoAnotherAspectRatioWithBlackBars()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("vertical.mp4");

        // 縦長（90×160）に書き出すと、上下に黒い帯が付く（中央の 90×50 にシーン全体が入る）
        await pipeline.ExportVideoAsync(
            new ExportOptions(path, Width: 90, Height: 160, To: 30, Encoder: EncoderPreference.Software), cancellationToken: TestContext.Current.CancellationToken);

        AssertStreams(path, "h264", 90, 160, 30);
        AssertRed(ColorAt(path, 15));
        AssertBlack(ColorAt(path, 15, 45, 10));
        AssertBlack(ColorAt(path, 15, 45, 150));
    }

    [Fact]
    public async Task CancellingDeletesThePartialFile()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("cancelled.mp4");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var progress = new SynchronousProgress<ExportProgress>(report =>
        {
            if (report.Frame == 10)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.ExportVideoAsync(new ExportOptions(path, Encoder: EncoderPreference.Software), progress, cancellation.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RejectsAnEmptyRange()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();

        await Assert.ThrowsAsync<ArgumentException>(
            () => pipeline.ExportVideoAsync(new ExportOptions(Output("empty.mp4"), From: 30, To: 30), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportsAudioAsWav()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("audio.wav");

        await pipeline.ExportWavAsync(path, from: 15, cancellationToken: TestContext.Current.CancellationToken);

        var wav = WavReader.Read(path);
        Assert.Equal((2, 48000, 72000L), (wav.Channels, wav.SampleRate, wav.FrameCount)); // 15〜60 フレーム目（1.5 秒）
        Assert.True(Rms(wav.Samples.AsSpan(0, 24000 * 2)) > 0.03); // 前の 0.5 秒は音がある
        Assert.True(Rms(wav.Samples.AsSpan(24000 * 2)) < 0.001); // 残りは無音
    }

    [Fact]
    public async Task NormalizesLoudness()
    {
        RequireFfmpeg();
        using var pipeline = CreatePipeline();
        var path = Output("loud.wav");

        await pipeline.ExportWavAsync(path, to: 30, loudnessTarget: -23, format: WavSampleFormat.Float32, cancellationToken: TestContext.Current.CancellationToken);

        var wav = WavReader.Read(path);
        Assert.InRange(Loudness.Integrated(wav.Samples, wav.SampleRate), -23.2, -22.8);
    }

    [Fact]
    public async Task ExportsStillImages()
    {
        using var pipeline = CreatePipeline();
        var png = Output("still.png");
        var jpeg = Output("still.jpg");

        await pipeline.ExportStillAsync(png, 45, cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.ExportStillAsync(jpeg, 10, width: 320, height: 180, cancellationToken: TestContext.Current.CancellationToken);

        using (var image = SKBitmap.Decode(png))
        {
            Assert.Equal((160, 90), (image.Width, image.Height));
            Assert.Equal(new SKColor(0, 0, 255), image.GetPixel(80, 45));
        }

        using (var image = SKBitmap.Decode(jpeg))
        {
            var color = image.GetPixel(160, 90);
            Assert.Equal(320, image.Width);
            Assert.True(color.Red > 240 && color.Green < 15 && color.Blue < 15, $"赤ではない: {color}");
        }

        Assert.Throws<ArgumentException>(() => ImageFiles.Save(new BgraImage(2, 2, new byte[16]), Output("still.gif")));
    }

    [Fact]
    public void PresetsCarryTheirSettings()
    {
        var options = ExportOptions.FromPreset("a.mp4", ExportPresets.Vertical);

        Assert.Equal((VideoCodecKind.H264, 1080, 1920, 10000), (options.Codec, options.Width, options.Height, options.VideoBitrateKbps));
        Assert.Equal(ExportPresets.All.Length, ExportPresets.All.Select(preset => preset.Id).Distinct().Count());
        Assert.Null(ExportOptions.FromPreset("b.mp4", ExportPresets.SceneSize).Width);
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    // Progress<T> と違い、報告したスレッドでそのまま呼ぶ
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
