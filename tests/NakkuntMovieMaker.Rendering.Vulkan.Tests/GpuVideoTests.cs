using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// 動画を描画と同じ GPU でデコードし、フレームをメインメモリに写さずに描く（F-31）。
/// 平面をメインメモリに写して送る流れと同じ絵になり、検証レイヤーのエラーが出ないことを確かめる
/// </summary>
public sealed class GpuVideoTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public GpuVideoTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void DrawsGpuDecodedFramesLikeCopiedPlanes()
    {
        Gpu.RequireVulkanVideoTests();
        using var device = Gpu.CreateOrSkip();
        Assert.SkipWhen(device.VideoDecode is null, "GPU が Vulkan Video のデコードに対応していないためスキップします");
        Gpu.RequireRenderer();
        VulkanDevice.ClearValidationMessages();

        var asset = new Asset(AssetId.New(), AssetKind.Video, Path.Combine(AppContext.BaseDirectory, "Fixtures", "frames-2997.mp4"));
        var empty = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        var project = empty with { Assets = empty.Assets.Add(asset.Id, asset) };
        using var media = new ProjectMediaSource(project, _folder, new MediaCache(Path.Combine(_folder, "cache")));
        using (var gpu = new VulkanBackend(device))
        {
            foreach (var frame in new long[] { 0, 7, 3, 30 })
            {
                var time = TimeMath.FrameToSeconds(frame, Rational.Fps29_97);
                var plan = new FramePlan(200, 120, new RgbaColor(10, 20, 30), new EquatableArray<RenderNode>(
                    [new DrawNode(null, new VideoFrameContent(asset.Id, time), Crop.None, Matrix3x2.CreateScale(1.2f) * Matrix3x2.CreateTranslation(100, 60), 1, BlendMode.Normal)]));

                var before = gpu.GpuVideoFrames;
                var decoded = gpu.Render(plan, media, 1, TestContext.Current.CancellationToken);
                Assert.True(gpu.GpuVideoFrames > before, $"GPU でデコードしたフレームを使っていません（{gpu.VideoDecodeProblem}）");
                var copied = gpu.Render(plan, new PlanesOnly(media), 1, TestContext.Current.CancellationToken);

                var difference = ImageCompare.Compare(copied, decoded);
                TestContext.Current.TestOutputHelper?.WriteLine($"{frame} フレーム目: {difference}");
                Assert.True(difference.Psnr >= 50, $"写して送る流れと違います（{difference}）");
            }
        }

        Assert.DoesNotContain(VulkanDevice.ValidationMessages, message => message.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void FallsBackToCopiedPlanesWithoutVulkanVideo()
    {
        // 既定（Vulkan Video を使わない）では、ハードウェアかソフトウェアでデコードした YUV の平面を写して送り、GPU で RGB にする。
        // 使っている方式は、素材ごとに分かる
        using var device = Gpu.CreateOrSkip(Gpu.Options with { VideoDecode = false });
        Gpu.RequireRenderer();
        var asset = new Asset(AssetId.New(), AssetKind.Video, Path.Combine(AppContext.BaseDirectory, "Fixtures", "frames-2997.mp4"));
        var empty = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        using var media = new ProjectMediaSource(empty with { Assets = empty.Assets.Add(asset.Id, asset) }, _folder, new MediaCache(Path.Combine(_folder, "cache")));
        using var gpu = new VulkanBackend(device);
        var plan = new FramePlan(200, 120, new RgbaColor(10, 20, 30), new EquatableArray<RenderNode>(
            [new DrawNode(null, new VideoFrameContent(asset.Id, Rational.Zero), Crop.None, Matrix3x2.CreateTranslation(100, 60), 1, BlendMode.Normal)]));
        Assert.SkipWhen(!FfmpegLibrary.TryLoad(out _, out var error), $"FFmpeg を読み込めないためスキップします（{error}）");

        var image = gpu.Render(plan, media, 1, TestContext.Current.CancellationToken);

        Assert.Equal(0, gpu.GpuVideoFrames);
        Assert.NotNull(gpu.VideoDecodeProblem);
        Assert.EndsWith("（YUV の平面）", media.VideoDecodeMethod(asset.Id));
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
    }

    // GPU のフレームを渡さない（平面をメインメモリに写して送る流れにする）
    private sealed class PlanesOnly(IMediaSource media) : IMediaSource
    {
        public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale) => media.GetVideoFrame(asset, time, scale);

        public SourceImage? GetImage(AssetId asset, double scale) => media.GetImage(asset, scale);

        public VideoPlaneFrame? GetVideoPlanes(AssetId asset, Rational time) => media.GetVideoPlanes(asset, time);
    }
}
