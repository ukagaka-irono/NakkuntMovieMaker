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

    // 素材を 1 つ登録したプロジェクトと、その素材の ID
    private (ProjectMediaSource Media, AssetId Asset) Open(string storedPath, AssetKind kind)
    {
        var asset = new Asset(AssetId.New(), kind, storedPath);
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        project = project with { Assets = project.Assets.Add(asset.Id, asset) };
        return (new ProjectMediaSource(project, _folder, new MediaCache(Path.Combine(_folder, ".nmm", "cache"))), asset.Id);
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
