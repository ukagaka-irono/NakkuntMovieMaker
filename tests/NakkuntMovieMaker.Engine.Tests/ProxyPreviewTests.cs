using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Engine.Tests;

/// <summary>
/// プロキシはプレビューだけで使い、書き出しでは必ず元の素材を使う（H-36）。
/// どちらを読んだかがわかるように、作ったプロキシのファイルを、中身の違う動画（赤い画面）に置き換えて確かめる
/// </summary>
public sealed class ProxyPreviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public ProxyPreviewTests() => Directory.CreateDirectory(Path.Combine(_folder, "media"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static void RequireFfmpeg()
    {
        var loaded = FfmpegLibrary.TryLoad(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") != "1", $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    // 黒い動画（frames-2997.mp4、128×96）を全面に置いたプロジェクト。プロキシはすべての動画に使う
    private (Project Project, AssetId Asset, string Source) CreateProject(ProxyMode mode = ProxyMode.AllVideos)
    {
        var source = Path.Combine(_folder, "media", "clip.mp4");
        File.Copy(Fixture("frames-2997.mp4"), source);
        var project = Project.CreateNew("テスト", new SceneFormat(128, 96, 30, 48000), 1) with { Proxies = new ProjectProxySettings(mode) };
        var asset = new Asset(AssetId.New(), AssetKind.Video, "media/clip.mp4");
        var scene = project.Root;
        var video = new VideoItem(ItemId.New(), scene.Layers[0].Id, 0, 30, asset.Id);
        return (project.WithScene(scene with { Items = scene.Items.Add(video.Id, video) }) with { Assets = project.Assets.Add(asset.Id, asset) }, asset.Id, source);
    }

    // プロキシを作り、中身を赤い画面の動画に置き換える
    private string ReplaceProxyWithRed(MediaCache cache, string source)
    {
        var proxy = cache.GetProxy(source, cancellationToken: TestContext.Current.CancellationToken);
        File.Copy(Fixture("red-blue-2s.mp4"), proxy, overwrite: true);
        return proxy;
    }

    private static bool IsRed(BgraImage image)
    {
        var (b, g, r, _) = image.GetPixel(image.Width / 2, image.Height / 2);
        return r > 200 && g < 60 && b < 60;
    }

    [Fact]
    public void PreviewReadsTheProxyButKeepsTheOriginalSize()
    {
        RequireFfmpeg();
        var (project, asset, source) = CreateProject();
        var cache = new MediaCache(Path.Combine(_folder, ".nmm", "cache"));
        ReplaceProxyWithRed(cache, source);
        using var proxies = new ProxyQueue(cache);
        using var media = new ProjectMediaSource(project, _folder, cache, proxies: proxies);

        var frame = media.GetVideoFrame(asset, Rational.Zero, 1);

        Assert.NotNull(frame);
        Assert.True(IsRed(frame.Image), "プレビューでプロキシを読んでいません");
        Assert.Equal((128, 96), (frame.Width, frame.Height)); // プロキシ（320×240）ではなく、元の素材の大きさ
        Assert.True(media.IsUsingProxy(asset));
    }

    [Fact]
    public async Task ExportNeverReadsProxies()
    {
        RequireFfmpeg();
        var (project, _, source) = CreateProject();
        ReplaceProxyWithRed(new MediaCache(Path.Combine(_folder, ".nmm", "cache")), source);
        var output = Path.Combine(_folder, "out.mp4");

        using (var pipeline = new ExportPipeline(project, project.RootScene, _folder))
        {
            await pipeline.ExportVideoAsync(new ExportOptions(output, Encoder: EncoderPreference.Software, To: 5), null, TestContext.Current.CancellationToken);
        }

        using var decoder = VideoDecoder.Open(output, new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));
        Assert.False(IsRed(decoder.GetFrame(Rational.Zero).Bgra), "書き出しでプロキシを読んでいます");
    }

    [Fact]
    public async Task CreatesMissingProxiesAndSwitchesToThem()
    {
        // プロキシがなければ、作り始めて、できるまでは元の素材を読む。できたら、次からプロキシを読む
        RequireFfmpeg();
        var (project, asset, source) = CreateProject();
        var cache = new MediaCache(Path.Combine(_folder, ".nmm", "cache"));
        using var proxies = new ProxyQueue(cache);
        var completed = new TaskCompletionSource();
        proxies.JobChanged += (_, job) =>
        {
            if (job.IsFinished)
            {
                completed.TrySetResult();
            }
        };
        using var media = new ProjectMediaSource(project, _folder, cache, proxies: proxies);

        Assert.NotNull(media.GetVideoFrame(asset, Rational.Zero, 1));
        Assert.False(media.IsUsingProxy(asset));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(media.IsUsingProxy(asset));
        Assert.NotNull(cache.FindProxy(source));
        Assert.NotNull(media.GetVideoFrame(asset, Rational.Zero, 1));
    }

    [Fact]
    public void LargeVideosModeSkipsSmallVideos()
    {
        // 既定（1080p より大きい動画だけ）では、小さい動画にはプロキシを作らない
        RequireFfmpeg();
        var (project, asset, source) = CreateProject(ProxyMode.LargeVideos);
        var cache = new MediaCache(Path.Combine(_folder, ".nmm", "cache"));
        using var proxies = new ProxyQueue(cache);
        using var media = new ProjectMediaSource(project, _folder, cache, proxies: proxies);

        Assert.NotNull(media.GetVideoFrame(asset, Rational.Zero, 1));

        Assert.Empty(proxies.ActiveJobs);
        Assert.Null(cache.FindProxy(source));
        Assert.False(media.IsUsingProxy(asset));
    }

    [Fact]
    public void UsesAnExistingProxyUnlessTurnedOff()
    {
        // 作ってあるプロキシは、小さい動画でも使う（自動で作らないだけ）。プレビューで切った・プロジェクトで使わないにしたら、元の素材を読む
        RequireFfmpeg();
        var (project, asset, source) = CreateProject(ProxyMode.LargeVideos);
        var cache = new MediaCache(Path.Combine(_folder, ".nmm", "cache"));
        ReplaceProxyWithRed(cache, source);
        using var proxies = new ProxyQueue(cache);
        using var media = new ProjectMediaSource(project, _folder, cache, proxies: proxies);

        Assert.True(IsRed(media.GetVideoFrame(asset, Rational.Zero, 1)!.Image));
        Assert.True(media.IsUsingProxy(asset));

        media.UseProxies = false;
        Assert.False(IsRed(media.GetVideoFrame(asset, Rational.Zero, 1)!.Image));
        Assert.False(media.IsUsingProxy(asset));

        media.UseProxies = true;
        Assert.True(IsRed(media.GetVideoFrame(asset, Rational.Zero, 1)!.Image));
        media.Update(project with { Proxies = new ProjectProxySettings(ProxyMode.Off) });
        Assert.False(IsRed(media.GetVideoFrame(asset, Rational.Zero, 1)!.Image));
        Assert.False(media.IsUsingProxy(asset));
        Assert.True(media.HasProxy(asset));
    }

    [Fact]
    public void DeletesTheProxyItIsReading()
    {
        // 読んでいるプロキシも、閉じてから消す。消したあとは、自動では作り直さない
        RequireFfmpeg();
        var (project, asset, source) = CreateProject();
        var cache = new MediaCache(Path.Combine(_folder, ".nmm", "cache"));
        var proxy = ReplaceProxyWithRed(cache, source);
        using var proxies = new ProxyQueue(cache);
        using var media = new ProjectMediaSource(project, _folder, cache, proxies: proxies);
        Assert.True(IsRed(media.GetVideoFrame(asset, Rational.Zero, 1)!.Image));

        Assert.True(media.DeleteProxy(asset));

        Assert.False(File.Exists(proxy));
        Assert.False(media.HasProxy(asset));
        Assert.False(IsRed(media.GetVideoFrame(asset, Rational.Zero, 1)!.Image));
        Assert.Empty(proxies.ActiveJobs);
        Assert.False(media.DeleteProxy(asset)); // もうない
    }
}
