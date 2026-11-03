using System.Collections.Concurrent;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Playback;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Tests;

public class FrameSchedulerTests
{
    // 16×9・30fps。frame 0〜99 は図形が 1 フレームに 1 ピクセルずつ動き（位置 X = 1000 + フレーム）、100〜199 は止まっている
    private static readonly SceneFormat Tiny = new(16, 9, 30, 48000);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Scene CreateScene()
    {
        var scene = Scene.CreateNew("テスト", Tiny, 1);
        var shape = new ShapeStyle(ShapeKind.Rectangle, 2, 2, new SolidPaint(RgbaColor.White));
        var moving = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 0, 100, shape)
        {
            Transform = Transform2D.Identity with { X = Animatable.FromKeys(new Keyframe<double>(0, 1000), new Keyframe<double>(100, 1100)) },
        };
        var still = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 100, 100, shape);
        return scene with { Items = scene.Items.Add(moving.Id, moving).Add(still.Id, still) };
    }

    private static FrameScheduler Create(FakeBackend backend, int workers = 2, long cacheBytes = 1 << 20, Scene? scene = null) =>
        new(scene ?? CreateScene(), backend, new NoMedia(), new FrameSchedulerOptions(workers, cacheBytes));

    [Fact]
    public async Task RendersRequestedFramesAndRemembersThem()
    {
        var backend = new FakeBackend();
        using var scheduler = Create(backend);

        var first = await scheduler.RequestAsync(10, Token);
        var again = await scheduler.RequestAsync(10, Token);

        Assert.Equal(10, first.Frame);
        Assert.Same(first.Image, again.Image);
        Assert.Equal([10L], backend.Rendered);
        Assert.True(scheduler.TryGetCached(10, out _));
        Assert.False(scheduler.TryGetCached(11, out _));
    }

    [Fact]
    public async Task SharesOneImageForFramesWithTheSameContent()
    {
        var backend = new FakeBackend();
        using var scheduler = Create(backend);

        var first = await scheduler.RequestAsync(120, Token);
        var second = await scheduler.RequestAsync(150, Token);

        Assert.Same(first.Image, second.Image);
        Assert.Equal(150, second.Frame);
        Assert.Single(backend.Rendered);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameFrameRenderOnce()
    {
        var backend = new FakeBackend { Delay = TimeSpan.FromMilliseconds(50) };
        using var scheduler = Create(backend, workers: 3);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => scheduler.RequestAsync(20, Token)));

        Assert.All(results, result => Assert.Same(results[0].Image, result.Image));
        Assert.Single(backend.Rendered);
    }

    [Fact]
    public async Task PrefetchRendersAheadInTheBackground()
    {
        var backend = new FakeBackend();
        using var scheduler = Create(backend);

        scheduler.Prefetch(30, 10);
        await scheduler.WaitForIdleAsync(Token);

        Assert.All(Enumerable.Range(30, 10), frame => Assert.True(scheduler.TryGetCached(frame, out _), $"{frame} を先読みしていない"));
        Assert.Equal(Enumerable.Range(30, 10).Select(frame => (long)frame), backend.Rendered.Order());
    }

    [Fact]
    public async Task NewPrefetchReplacesPendingOnesAndRequestsGoFirst()
    {
        var backend = new FakeBackend();
        backend.Gate.Reset();
        using var scheduler = Create(backend, workers: 1);

        // 1 つのスレッドが 0 を描いている途中で、先読みをやり直し、さらに 50 を求める
        scheduler.Prefetch(0, 10);
        Assert.True(backend.Started.Wait(TimeSpan.FromSeconds(5), Token));
        scheduler.Prefetch(80, 3);
        var request = scheduler.RequestAsync(50, Token);
        backend.Gate.Set();
        await request;
        await scheduler.WaitForIdleAsync(Token);

        Assert.Equal([0L, 50, 80, 81, 82], backend.Rendered);
    }

    [Fact]
    public async Task EditedFramesAreRenderedAgain()
    {
        var backend = new FakeBackend();
        var scene = CreateScene();
        using var scheduler = Create(backend, scene: scene);
        await scheduler.RequestAsync(10, Token);
        await scheduler.RequestAsync(150, Token);

        // 動く図形を消すと 10 フレーム目の内容が変わり、止まっている区間はそのまま使える
        var moving = scene.Items.Values.Single(item => item.Start == 0);
        scheduler.SetScene(scene with { Items = scene.Items.Remove(moving.Id) });
        await scheduler.RequestAsync(10, Token);
        await scheduler.RequestAsync(150, Token);

        Assert.Equal(3, backend.Rendered.Count);
    }

    [Fact]
    public async Task InvalidateDropsFramesInTheRange()
    {
        var backend = new FakeBackend();
        var scene = CreateScene();
        using var scheduler = Create(backend, scene: scene);
        await scheduler.RequestAsync(10, Token);
        await scheduler.RequestAsync(150, Token);

        scheduler.Invalidate([new DirtyRange(scene.Id, 0, 50)]);
        Assert.False(scheduler.TryGetCached(10, out _));
        Assert.True(scheduler.TryGetCached(150, out _));

        // 素材の差し替えなど、すべてのシーンにかかる変更ではすべて捨てる
        scheduler.Invalidate([DirtyRange.Everything]);
        Assert.False(scheduler.TryGetCached(150, out _));
        Assert.Equal(0, scheduler.Cache.Count);
    }

    [Fact]
    public async Task ConnectFollowsDocumentEdits()
    {
        var backend = new FakeBackend();
        var project = Project.CreateNew("テスト", Tiny, 1);
        var document = new ProjectDocument(project.WithScene(CreateScene() with { Id = project.RootScene }));
        using var scheduler = Create(backend, scene: document.Current.Root);
        using var connection = scheduler.Connect(document, document.Current.RootScene);
        await scheduler.RequestAsync(10, Token);

        var moving = document.Current.Root.Items.Values.Single(item => item.Start == 0);
        document.Execute(EditCommands.DeleteItems(document.Current, document.Current.RootScene, [moving.Id]));

        Assert.Equal(document.Current.Root, scheduler.Scene);
        Assert.False(scheduler.TryGetCached(10, out _));
    }

    [Fact]
    public async Task ForgetsTheOldestFramesWhenFull()
    {
        var backend = new FakeBackend();

        // 1 枚は 16×9×4 = 576 バイト。3 枚までしか覚えない
        using var scheduler = Create(backend, cacheBytes: 576 * 3);
        foreach (var frame in new[] { 1, 2, 3, 1, 4 })
        {
            await scheduler.RequestAsync(frame, Token);
        }

        Assert.True(scheduler.TryGetCached(1, out _)); // 最近使ったので残る
        Assert.False(scheduler.TryGetCached(2, out _));
        Assert.Equal(576 * 3, scheduler.Cache.Bytes);
    }

    [Fact]
    public async Task ChangingTheScaleRendersAgain()
    {
        var backend = new FakeBackend();
        using var scheduler = Create(backend);
        await scheduler.RequestAsync(10, Token);

        scheduler.Scale = 0.5;
        var half = await scheduler.RequestAsync(10, Token);

        Assert.Equal((8, 4), (half.Image.Width, half.Image.Height)); // 9 × 0.5 = 4.5 は偶数への丸めで 4
        Assert.Equal(2, backend.Rendered.Count);
    }

    [Fact]
    public async Task ReportsRenderErrorsAndCancellation()
    {
        var backend = new FakeBackend { Fail = true };
        using var scheduler = Create(backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RequestAsync(5, Token));

        backend.Fail = false;
        backend.Gate.Reset();
        using var cancel = new CancellationTokenSource();
        var pending = scheduler.RequestAsync(6, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        backend.Gate.Set();
    }

    // 描いたフレームの番号を記録する描画の処理（番号は図形の位置から求める）
    private sealed class FakeBackend : IRenderBackend
    {
        private readonly ConcurrentQueue<long> _rendered = new();

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public ManualResetEventSlim Started { get; } = new();

        public TimeSpan Delay { get; init; }

        public bool Fail { get; set; }

        public List<long> Rendered => [.. _rendered];

        public BgraImage Render(FramePlan plan, IMediaSource media, double scale = 1, CancellationToken cancellationToken = default)
        {
            Started.Set();
            Gate.Wait(cancellationToken);
            Thread.Sleep(Delay);
            if (Fail)
            {
                throw new InvalidOperationException("描けませんでした");
            }

            var node = plan.Nodes.OfType<DrawNode>().FirstOrDefault();
            var offset = node is null ? -1 : (long)Math.Round(node.Transform.M31 - plan.Width / 2.0);
            var frame = offset >= 1000 ? offset - 1000 : 100; // 止まっている区間は 100 として記録する
            _rendered.Enqueue(frame);
            var width = Math.Max(1, (int)Math.Round(plan.Width * scale));
            var height = Math.Max(1, (int)Math.Round(plan.Height * scale));
            return new BgraImage(width, height, new byte[width * height * 4]);
        }
    }

    private sealed class NoMedia : IMediaSource
    {
        public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale) => null;

        public SourceImage? GetImage(AssetId asset, double scale) => null;
    }
}
