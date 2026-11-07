using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Playback;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Tests;

/// <summary>
/// 再生。音は音を出さない仮のデバイス（miniaudio の null バックエンド）で鳴らし、実時間で進む時計を確かめる。
/// 時間を測るテストなので、許す誤差は大きめにしてある
/// </summary>
public class PlaybackControllerTests
{
    // 64×36・30fps・48kHz
    private static readonly SceneFormat Small = new(64, 36, 30, 48000);

    private static void RequireNativeAudio()
    {
        var loaded = NativeAudio.TryGetInfo(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_NMM_AUDIO") != "1", $"nmm_audio を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    // frames フレームの長さの、音声と図形のあるシーン
    private static Scene CreateScene(long frames)
    {
        var scene = Scene.CreateNew("テスト", Small, 2);
        var audio = new AudioItem(ItemId.New(), scene.Layers[0].Id, 0, frames, AssetId.New());
        var shape = new ShapeItem(ItemId.New(), scene.Layers[1].Id, 0, frames, new ShapeStyle(ShapeKind.Rectangle, 10, 10, new SolidPaint(RgbaColor.White)));
        return scene with { Items = scene.Items.Add(audio.Id, audio).Add(shape.Id, shape) };
    }

    private static (PlaybackController Playback, FrameScheduler Frames) Create(Scene scene, PlaybackOptions? options = null)
    {
        var frames = new FrameScheduler(scene, new BlankBackend(), new NoMedia(), new FrameSchedulerOptions(2, 1 << 24));
        return (new PlaybackController(scene, new ToneSource(), frames, options ?? new PlaybackOptions(NullAudioDevice: true)), frames);
    }

    [Fact]
    public void ClockFollowsTheAudioDevice()
    {
        RequireNativeAudio();
        var (playback, frames) = Create(CreateScene(300));
        using var scheduler = frames;
        using var player = playback;
        Assert.True(playback.HasAudioOutput, playback.AudioError);

        playback.Play();
        Thread.Sleep(1000);
        var playing = playback.CurrentSample;
        playback.Pause();
        var paused = playback.CurrentSample;
        Thread.Sleep(200);

        Assert.InRange(playing, 48000 * 0.7, 48000 * 1.15);
        Assert.Equal(paused, playback.CurrentSample); // 止めたら進まない
        Assert.False(playback.IsPlaying);
        Assert.True(playback.Statistics.UnderrunFrames < 4800, $"音が足りなかったフレーム: {playback.Statistics.UnderrunFrames}");

        // 続きから再生する
        playback.Play();
        Thread.Sleep(300);
        Assert.True(playback.CurrentSample > paused);
        playback.Pause();
    }

    [Fact]
    public void SeekMovesTheClock()
    {
        RequireNativeAudio();
        var (playback, frames) = Create(CreateScene(600));
        using var scheduler = frames;
        using var player = playback;

        playback.Seek(90);
        Assert.Equal(90, playback.CurrentFrame);

        playback.Play();
        Thread.Sleep(200);
        playback.Seek(300);
        Thread.Sleep(200);
        Assert.InRange(playback.CurrentFrame, 300, 312);
        playback.Pause();
    }

    [Fact]
    public void StopsAtTheEnd()
    {
        RequireNativeAudio();
        var (playback, frames) = Create(CreateScene(9)); // 0.3 秒
        using var scheduler = frames;
        using var player = playback;
        using var ended = new ManualResetEventSlim();
        playback.Ended += (_, _) => ended.Set();

        playback.Play();

        Assert.True(ended.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False(playback.IsPlaying);
        Assert.Equal(9, playback.CurrentFrame);

        // 最後まで行った後に再生すると、先頭から
        playback.Play();
        Assert.InRange(playback.CurrentFrame, 0, 3);
        playback.Pause();
    }

    [Fact]
    public void PlaysWithoutAnAudioDevice()
    {
        var (playback, frames) = Create(CreateScene(300), new PlaybackOptions(DisableAudioDevice: true));
        using var scheduler = frames;
        using var player = playback;

        Assert.False(playback.HasAudioOutput);
        playback.Play();
        Thread.Sleep(500);
        Assert.InRange(playback.CurrentSample, 48000 * 0.4, 48000 * 0.7);
        playback.Pause();
    }

    [Fact]
    public async Task ProvidesFramesForTheClock()
    {
        RequireNativeAudio();
        var (playback, frames) = Create(CreateScene(300));
        using var scheduler = frames;
        using var player = playback;

        // 止まっているときは、描いてあれば返す
        Assert.False(playback.TryGetFrame(out var _1));
        await frames.RequestAsync(0, TestContext.Current.CancellationToken);
        Assert.True(playback.TryGetFrame(out var first));
        Assert.Equal(0, first.Frame);

        // 再生中は、先読みしたフレームを画面の更新ごとに取り出す
        playback.Play();
        var shown = 0;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(16, TestContext.Current.CancellationToken);
            if (playback.TryGetFrame(out _))
            {
                shown++;
            }
        }

        playback.Pause();
        Assert.True(shown > 15, $"表示できたのは {shown} 回");
        Assert.True(playback.Statistics.ShownFrames > 10);
    }

    // どの素材にも 440Hz の音を返す
    [Fact]
    public void ReportsTheLevelOfWhatIsPlaying()
    {
        RequireNativeAudio();
        var (playback, frames) = Create(CreateScene(300));
        using var scheduler = frames;
        using var player = playback;
        Assert.Equal(StereoLevel.Silent, playback.Level);

        playback.Play();
        Thread.Sleep(300);
        var level = playback.Level;
        playback.Pause();

        // 振幅 0.2 のサイン波（-14 dB）。パンは中央なので左右に少し下げて分ける
        Assert.InRange(level.Left.PeakDb, -20, -10);
        Assert.InRange(level.Right.RmsDb, -25, -13);
        Assert.Equal(StereoLevel.Silent, playback.Level);
    }

    private sealed class ToneSource : IAudioSource
    {
        private readonly float[] _tone = [.. Enumerable.Range(0, 48000 * 20).SelectMany(i => Enumerable.Repeat((float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / 48000)), 2))];

        public IAudioStream? OpenAudio(AssetId asset) => new MemoryAudioStream(_tone, 2, 48000);
    }

    // 何も描かない描画の処理（時間のかからない代わり）
    private sealed class BlankBackend : IRenderBackend
    {
        public RenderBackendInfo Info { get; } = new(RenderBackendKind.Cpu, "テスト", null, 1);

        public void Dispose()
        {
        }

        public RenderResult Render(FramePlan plan, IMediaSource media, RenderRequest request, CancellationToken cancellationToken = default) =>
            new CpuRenderResult(new BgraImage(plan.Width, plan.Height, new byte[plan.Width * plan.Height * 4]));
    }

    private sealed class NoMedia : IMediaSource
    {
        public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale) => null;

        public SourceImage? GetImage(AssetId asset, double scale) => null;
    }
}
