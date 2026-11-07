using NakkuntMovieMaker.ColorManagement;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Benchmarks;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Engine.Tests;

public sealed class ProjectMediaSourceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public ProjectMediaSourceTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static void RequireFfmpeg()
    {
        var loaded = FfmpegLibrary.TryLoad(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") != "1", $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static void RequireOcio()
    {
        var loaded = Ocio.TryLoad(out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_OCIO") != "1", $"nmm_ocio を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    // 素材を 1 つ登録したプロジェクトと、その素材の ID
    private (ProjectMediaSource Media, AssetId Asset) Open(string storedPath, AssetKind kind, string? colorSpace = null)
    {
        var asset = new Asset(AssetId.New(), kind, storedPath, ColorSpace: colorSpace);
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        project = project with { Assets = project.Assets.Add(asset.Id, asset) };
        return (new ProjectMediaSource(project, _folder, new MediaCache(Path.Combine(_folder, ".nmm", "cache"))), asset.Id);
    }

    [Fact]
    public void ImagesWithoutAColorSpaceAreNotConverted()
    {
        // 既定（入力の色空間の指定なし）では、色を変換しない（OpenColorIO がなくても描ける）
        RequireFfmpeg();
        var (media, asset) = Open(Fixture("green-64x48.png"), AssetKind.Image);
        using var _ = media;

        var image = media.GetImage(asset, 1)!;

        Assert.Equal(image.Image.Pixels, new MediaCache(Path.Combine(_folder, ".nmm", "cache")).GetImage(Fixture("green-64x48.png"), 64, 48).Pixels);
        Assert.Null(media.InputColorError(asset));
    }

    [Fact]
    public void ConvertsImagesFromTheirInputColorSpace()
    {
        // ガンマ 2.2 の素材は、作業用の sRGB にすると、中間の明るさが少し変わる（純色の 255 は変わらない）
        RequireFfmpeg();
        RequireOcio();
        var (plain, plainAsset) = Open(Fixture("green-64x48.png"), AssetKind.Image);
        var (converted, convertedAsset) = Open(Fixture("green-64x48.png"), AssetKind.Image, "Gamma 2.2 Encoded Rec.709");
        using var a = plain;
        using var b = converted;

        var before = plain.GetImage(plainAsset, 1)!.Image;
        var after = converted.GetImage(convertedAsset, 1)!.Image;

        Assert.Null(converted.InputColorError(convertedAsset));
        Assert.Equal(before.Pixels.Length, after.Pixels.Length);
        Assert.NotEqual(before.Pixels, after.Pixels);
        Assert.All(Enumerable.Range(0, before.Pixels.Length / 4), i => Assert.InRange(after.Pixels[i * 4 + 1] - before.Pixels[i * 4 + 1], -12, 12));
    }

    [Fact]
    public void ReportsUnknownInputColorSpaces()
    {
        RequireFfmpeg();
        RequireOcio();
        var (media, asset) = Open(Fixture("green-64x48.png"), AssetKind.Image, "存在しない色空間");
        using var _ = media;

        Assert.NotNull(media.GetImage(asset, 1)); // 変換できなければ、そのまま描く
        Assert.Contains("存在しない色空間", media.InputColorError(asset));
    }

    [Fact]
    public void ReturnsImagesScaledToTheNeededStep()
    {
        RequireFfmpeg();
        var (media, asset) = Open(Fixture("green-64x48.png"), AssetKind.Image);
        using var _ = media;

        var full = media.GetImage(asset, 1);
        var reduced = media.GetImage(asset, 0.3); // 0.3 倍で足りるので、1/2 の段階を使う

        Assert.NotNull(full);
        Assert.NotNull(reduced);
        Assert.Equal((64, 48, 64, 48), (full.Image.Width, full.Image.Height, full.Width, full.Height));
        Assert.Equal((32, 24, 64, 48), (reduced.Image.Width, reduced.Image.Height, reduced.Width, reduced.Height));
        Assert.Same(reduced, media.GetImage(asset, 0.4));
    }

    [Fact]
    public void ReturnsVideoFramesFromProjectRelativePaths()
    {
        RequireFfmpeg();
        Directory.CreateDirectory(Path.Combine(_folder, "media"));
        File.Copy(Fixture("frames-2997.mp4"), Path.Combine(_folder, "media", "clip.mp4"));
        var (media, asset) = Open("media/clip.mp4", AssetKind.Video);
        using var _ = media;

        var frame = media.GetVideoFrame(asset, TimeMath.FrameToSeconds(10, Rational.Fps29_97), 1);

        Assert.NotNull(frame);
        Assert.Equal((128, 96), (frame.Width, frame.Height));
        // フレーム番号は色で埋め込んである（赤 = (N mod 8)×32+16、緑 = (N÷8)×32+16）
        var (_, g, r, _) = frame.Image.GetPixel(64, 48);
        Assert.Equal(10, (int)Math.Round((r - 16) / 32.0) + 8 * (int)Math.Round((g - 16) / 32.0));
    }

    [Fact]
    public void OpensAudioThroughThePcmCache()
    {
        RequireFfmpeg();
        var (media, asset) = Open(Fixture("tone-1s.wav"), AssetKind.Audio);
        using var _ = media;

        var audio = media.OpenAudio(asset);

        Assert.NotNull(audio);
        Assert.Equal((48000, 1, 48000L), (audio.SampleRate, audio.Channels, audio.FrameCount));
        Assert.Null(media.OpenAudio(AssetId.New()));
    }

    [Fact]
    public void OpensSynthesizedVoicesFromTheProject()
    {
        RequireFfmpeg();
        Directory.CreateDirectory(Path.Combine(_folder, "voices"));
        Audio.WavWriter.WriteFile(Path.Combine(_folder, "voices", "abc.wav"), new float[24000], 1, 24000);
        var (media, _) = Open("x.png", AssetKind.Image);
        using var _2 = media;

        var voice = media.OpenVoice("abc");

        // 24kHz の 1 秒は、48kHz の PCM キャッシュで 48000 サンプル
        Assert.NotNull(voice);
        Assert.Equal((48000, 48000L), (voice.SampleRate, voice.FrameCount));
        Assert.Null(media.OpenVoice("missing"));
    }

    [Fact]
    public void ReturnsNullForMissingMedia()
    {
        RequireFfmpeg();
        var (media, asset) = Open("missing.png", AssetKind.Image);
        using var _ = media;

        Assert.Null(media.GetImage(asset, 1));
        Assert.Null(media.GetVideoFrame(asset, Rational.Zero, 1));
        Assert.Null(media.GetImage(AssetId.New(), 1));
    }

    [Fact]
    public void ReloadsAssetsWhosePathChanged()
    {
        RequireFfmpeg();
        var asset = new Asset(AssetId.New(), AssetKind.Image, Fixture("green-64x48.png"));
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        project = project with { Assets = project.Assets.Add(asset.Id, asset) };
        using var media = new ProjectMediaSource(project, _folder, new MediaCache(Path.Combine(_folder, ".nmm", "cache")));
        Assert.NotNull(media.GetImage(asset.Id, 1));

        // リンクが切れた素材は、覚えていた画像を捨てて描かない
        media.Update(project with { Assets = project.Assets.SetItem(asset.Id, asset with { Path = "missing.png" }) });
        Assert.Null(media.GetImage(asset.Id, 1));

        // 再指定すると、また描ける
        media.Update(project);
        Assert.NotNull(media.GetImage(asset.Id, 1));
    }

    [Fact]
    public void ResolvesRelativePathsAgainstTheProjectFolder()
    {
        var (media, _) = Open("x.png", AssetKind.Image);
        using var _2 = media;

        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "luts", "warm.cube")), media.ResolvePath("luts/warm.cube"));
        var absolute = Path.Combine(_folder, "a.cube");
        Assert.Equal(absolute, media.ResolvePath(absolute));
    }

    [Fact]
    public void BenchmarkRunsAndReportsTimes()
    {
        RequireFfmpeg();
        var benchmark = new RenderBenchmark(_folder, video: null);

        var result = benchmark.Run(RenderBenchmark.Scenarios.Single(scenario => scenario.Name == "half-effects"), frames: 6, workers: 2, TestContext.Current.CancellationToken);

        Assert.Equal(6, result.Frames);
        Assert.True(result.MeanMilliseconds > 0 && result.FramesPerSecond > 0);
        Assert.True(result.P95Milliseconds <= result.MaxMilliseconds);
    }
}
