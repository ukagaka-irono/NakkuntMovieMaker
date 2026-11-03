using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Playback;

namespace NakkuntMovieMaker.Engine.Tests;

public sealed class RecordingTests : IDisposable
{
    // 30fps・48kHz：1 フレームは 1600 サンプル
    private static readonly SceneFormat Format = SceneFormat.Landscape1080p30;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public RecordingTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static RecordingResult Result(long placement, long frames) => new("take.wav", frames, 48000, 1, placement, 0);

    [Theory]
    [InlineData(48000, 48000, 30, 30, 0)] // フレームの頭にぴったり：1 秒（30 フレーム目）から 1 秒
    [InlineData(48400, 48000, 31, 30, 1200)] // フレームの途中：次のフレーム（31）から。手前の 1200 サンプルを削る
    [InlineData(-800, 3200, 0, 2, 800)] // 0 より前：0 から。手前の 800 サンプルを削る
    public void AlignsTheRecordingToTheSample(long placement, long frames, long start, long length, long skipped)
    {
        var timing = Recording.Timing(Result(placement, frames), Format);

        Assert.Equal((start, length, new Rational(skipped, 48000)), timing);
    }

    [Fact]
    public void AddsTheRecordingToTheTimeline()
    {
        var project = Project.CreateNew("テスト", Format, 2);
        var layer = project.Root.Layers[1].Id;
        var path = Path.Combine(_folder, "recordings", "take.wav");
        var result = new RecordingResult(path, 96000, 48000, 1, 50000, 0);

        var (operation, itemId) = Recording.AddToTimeline(project, project.RootScene, layer, result, _folder);
        var edited = operation.Apply(project).Project;

        var item = Assert.IsType<AudioItem>(edited.Root.GetItem(itemId));
        var asset = edited.Assets[item.Asset];
        Assert.Equal((AssetKind.Audio, "recordings/take.wav"), (asset.Kind, asset.Path));
        Assert.Equal(layer, item.Layer);

        // 鳴る位置は、置く位置（50000 サンプル）とサンプル単位で一致する
        var clip = Assert.Single(AudioEvaluator.Build(edited.Root).Clips);
        Assert.Equal(50000, clip.Start - (clip.SourceOffset * 48000).ToInt64(Rounding.Nearest));
        Assert.True(clip.End >= 50000 + 96000 - 1600, "録った音の最後まで入る");
    }

    [Fact]
    public void NamesRecordingsByDateWithoutOverwriting()
    {
        var now = new DateTimeOffset(2026, 11, 4, 9, 30, 15, TimeSpan.Zero);

        var first = Recording.NewPath(_folder, now);
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllText(first, "");
        var second = Recording.NewPath(_folder, now);

        Assert.Equal(Path.Combine(_folder, "recordings"), Path.GetDirectoryName(first));
        Assert.EndsWith(".wav", first);
        Assert.NotEqual(first, second);
        Assert.EndsWith("_2.wav", second);
    }

    [Fact]
    public void CalibrationReportsNothingWhenNoSoundComesBack()
    {
        var loaded = NativeAudio.TryGetInfo(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_NMM_AUDIO") != "1", $"nmm_audio を読み込めないためスキップします（{error}）");

        // 仮のデバイスは出力の音を入力に返さないので、クリックは録れない
        Assert.Null(LatencyCalibration.Measure(nullDevices: true, cancellationToken: TestContext.Current.CancellationToken));
    }
}
