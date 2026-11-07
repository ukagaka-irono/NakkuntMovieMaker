using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Playback;

/// <summary>描いたフレーム。Image は共有するので、書き換えないこと</summary>
public sealed record RenderedFrame(long Frame, BgraImage Image, FramePlan Plan);

/// <summary>
/// FrameScheduler の設定。Workers は並列に描くスレッドの数、CacheBytes は描いたフレームを覚えておく大きさ、
/// Background はシーンの背景の色
/// </summary>
public sealed record FrameSchedulerOptions(int Workers = 3, long CacheBytes = 512L << 20, RgbaColor? Background = null)
{
    /// <summary>CPU のコア数に合わせた既定の設定（描くスレッドは 1〜3 個。デコードや画面のためにコアを残す）</summary>
    public static FrameSchedulerOptions Default { get; } = new(Math.Clamp(Environment.ProcessorCount / 2 - 1, 1, 3));
}

/// <summary>
/// プレビューのフレームを描く順番を決める。描いたフレームは FrameCache に覚えておく。
/// <list type="bullet">
/// <item>Request：今すぐ表示するフレーム。先読みより先に描く</item>
/// <item>Prefetch：再生中に、この先のフレームを順番に描いておく。新しく Prefetch すると、まだ描いていない前の先読みは取りやめる</item>
/// </list>
/// 別々のフレームを、複数のスレッドで並列に描く（同じ動画のデコードは、素材の受け渡し側で順番に行う）。
/// シーンを編集したら SetScene と Invalidate を呼ぶ（ProjectDocument につなぐ場合は Connect が行う）。
/// 複数のスレッドから同時に使ってよい
/// </summary>
public sealed class FrameScheduler : IDisposable
{
    private readonly IRenderBackend _backend;
    private readonly IMediaSource _media;
    private readonly FrameSchedulerOptions _options;
    private readonly FrameCache _cache;
    private readonly Lock _gate = new();
    private readonly LinkedList<Job> _requests = new();
    private readonly LinkedList<Job> _prefetches = new();
    private readonly Dictionary<(FramePlan Plan, double Scale), Task<RenderedFrame>> _running = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread[] _threads;
    private Scene _scene;
    private TimelineContext _timeline;
    private double _scale = 1;
    private int _busy;
    private TaskCompletionSource _idle = CompletedSource();

    /// <param name="timeline">キャラクター・立ち絵・口パク（セリフの字幕と立ち絵に使う）</param>
    public FrameScheduler(
        Scene scene,
        IRenderBackend backend,
        IMediaSource media,
        FrameSchedulerOptions? options = null,
        TimelineContext? timeline = null)
    {
        _scene = scene;
        _timeline = timeline ?? TimelineContext.Empty;
        _backend = backend;
        _media = media;
        _options = options ?? FrameSchedulerOptions.Default;
        _cache = new FrameCache(_options.CacheBytes);
        _threads = [.. Enumerable.Range(0, Math.Max(1, _options.Workers)).Select(index => new Thread(Work)
        {
            IsBackground = true,
            Name = $"フレームの描画 {index + 1}",
        })];
        foreach (var thread in _threads)
        {
            thread.Start();
        }
    }

    public FrameCache Cache => _cache;

    public Scene Scene
    {
        get
        {
            lock (_gate)
            {
                return _scene;
            }
        }
    }

    /// <summary>出力の倍率（プレビューの 1/2 なら 0.5）。変えると、まだ描いていない先読みは取りやめる</summary>
    public double Scale
    {
        get
        {
            lock (_gate)
            {
                return _scale;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (_gate)
            {
                _scale = value;
                _prefetches.Clear();
            }
        }
    }

    /// <summary>編集した後のシーンにする（まだ描いていないフレームは、新しいシーンで描く）</summary>
    public void SetScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            _scene = scene;
        }
    }

    /// <summary>キャラクターを変えた後の一覧にする</summary>
    public void SetCharacters(IReadOnlyDictionary<CharacterId, Character> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        lock (_gate)
        {
            _timeline = _timeline with { Characters = characters };
        }
    }

    /// <summary>色の設定を変える（まだ描いていないフレームは、新しい設定で描く。設定は FramePlan に入るので、前の設定で描いたフレームは使わない）</summary>
    public void SetColor(FrameColor color)
    {
        ArgumentNullException.ThrowIfNull(color);
        lock (_gate)
        {
            _timeline = _timeline with { Color = color };
        }
    }

    /// <summary>描き直しが必要になった範囲のフレームを、覚えているものから捨てる</summary>
    public void Invalidate(IEnumerable<DirtyRange> ranges) => _cache.Invalidate(ranges);

    /// <summary>覚えているフレームがあれば返す（描くのを待たない。再生中に使う）</summary>
    public bool TryGetCached(long frame, out RenderedFrame rendered)
    {
        var (scene, scale) = Snapshot();
        var plan = Evaluate(scene, frame);
        if (_cache.TryGet(scene.Id, frame, plan, scale, out var image))
        {
            rendered = new RenderedFrame(frame, image, plan);
            return true;
        }

        rendered = null!;
        return false;
    }

    /// <summary>フレームを描いて返す（覚えていればすぐ返す）。先読みより先に描く</summary>
    public Task<RenderedFrame> RequestAsync(long frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        var (scene, scale) = Snapshot();
        var plan = Evaluate(scene, frame);
        if (_cache.TryGet(scene.Id, frame, plan, scale, out var cached))
        {
            return Task.FromResult(new RenderedFrame(frame, cached, plan));
        }

        Task<RenderedFrame> task;
        lock (_gate)
        {
            if (!_running.TryGetValue((plan, scale), out var running))
            {
                var job = new Job(frame, scene, plan, scale);
                _requests.AddLast(job);
                _running[(plan, scale)] = job.Completion.Task;
                running = job.Completion.Task;
                MarkBusy();
                _signal.Release();
            }

            task = running;
        }

        return WithFrame(task, frame, cancellationToken);
    }

    // 同じ内容のフレームを別のフレーム番号で求められたときも、番号だけ合わせて返す
    private static async Task<RenderedFrame> WithFrame(Task<RenderedFrame> task, long frame, CancellationToken cancellationToken)
    {
        var rendered = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return rendered.Frame == frame ? rendered : rendered with { Frame = frame };
    }

    /// <summary>
    /// from から count 枚を、順番に先読みする。まだ描いていない前の先読みは取りやめる
    /// （再生を始めた・シークしたときに呼ぶ）
    /// </summary>
    public void Prefetch(long from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_gate)
        {
            _prefetches.Clear();
            for (var frame = from; frame < from + count; frame++)
            {
                _prefetches.AddLast(new Job(frame, null, null, 0));
                _signal.Release();
            }

            if (count > 0)
            {
                MarkBusy();
            }
        }
    }

    /// <summary>まだ描いていない先読みを取りやめる（再生を止めたときに呼ぶ）</summary>
    public void CancelPrefetch()
    {
        lock (_gate)
        {
            _prefetches.Clear();
        }
    }

    /// <summary>受け付けたフレームをすべて描き終えるまで待つ</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 編集のたびにシーンを差し替え、変わった範囲を捨てるようにつなぐ。
    /// 素材の受け渡しが ProjectMediaSource なら、素材の一覧も新しくする。戻り値を破棄すると切り離す
    /// </summary>
    public IDisposable Connect(ProjectDocument document, SceneId scene)
    {
        void OnChanged(object? sender, DocumentChange change)
        {
            if (_media is ProjectMediaSource projectMedia)
            {
                projectMedia.Update(change.Current);
            }

            if (change.Current.Scenes.TryGetValue(scene, out var current))
            {
                SetScene(current);
            }

            SetCharacters(change.Current.Characters);

            Invalidate(change.Dirty);
        }

        document.Changed += OnChanged;
        return new Subscription(() => document.Changed -= OnChanged);
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        _signal.Release(_threads.Length);
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        lock (_gate)
        {
            foreach (var job in _requests)
            {
                job.Completion.TrySetCanceled();
            }

            _requests.Clear();
            _prefetches.Clear();
            _idle.TrySetResult();
        }

        _signal.Dispose();
        _shutdown.Dispose();
    }

    private (Scene Scene, double Scale) Snapshot()
    {
        lock (_gate)
        {
            return (_scene, _scale);
        }
    }

    private FramePlan Evaluate(Scene scene, long frame)
    {
        TimelineContext timeline;
        lock (_gate)
        {
            timeline = _timeline;
        }

        return TimelineEvaluator.Evaluate(scene, frame, _options.Background, timeline: timeline);
    }

    private void Work()
    {
        var token = _shutdown.Token;
        while (true)
        {
            try
            {
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Job? job;
            lock (_gate)
            {
                job = Take(_requests) ?? Take(_prefetches);
                if (job is null)
                {
                    CheckIdle();
                    continue;
                }

                _busy++;
            }

            try
            {
                if (job.Plan is null)
                {
                    RunPrefetch(job.Frame, token);
                }
                else
                {
                    RunRequest(job, token);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _busy--;
                    CheckIdle();
                }
            }
        }
    }

    private void RunRequest(Job job, CancellationToken token)
    {
        try
        {
            var image = _backend.Render(job.Plan!, _media, job.Scale, token);
            _cache.Add(job.Scene!.Id, job.Frame, job.Plan!, job.Scale, image);
            job.Completion.TrySetResult(new RenderedFrame(job.Frame, image, job.Plan!));
        }
        catch (OperationCanceledException)
        {
            job.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            job.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove((job.Plan!, job.Scale));
            }
        }
    }

    // 先読みは、取り出した時点のシーンで描く。覚えている・ほかのスレッドが描いているフレームは飛ばす
    private void RunPrefetch(long frame, CancellationToken token)
    {
        var (scene, scale) = Snapshot();
        var plan = Evaluate(scene, frame);
        if (_cache.TryGet(scene.Id, frame, plan, scale, out _))
        {
            return;
        }

        lock (_gate)
        {
            if (_running.ContainsKey((plan, scale)))
            {
                return;
            }
        }

        var job = new Job(frame, scene, plan, scale);
        lock (_gate)
        {
            _running[(plan, scale)] = job.Completion.Task;
        }

        RunRequest(job, token);

        // 先読みの失敗は、表示するときに求め直せばよいので捨てる（例外を見たことにして、警告を出さない）
        _ = job.Completion.Task.Exception;
    }

    private static Job? Take(LinkedList<Job> queue)
    {
        if (queue.First is not { } first)
        {
            return null;
        }

        queue.RemoveFirst();
        return first.Value;
    }

    private void MarkBusy()
    {
        if (_idle.Task.IsCompleted)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void CheckIdle()
    {
        if (_busy == 0 && _requests.Count == 0 && _prefetches.Count == 0)
        {
            _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    // 求められたフレーム（Plan は求めた時点の内容）か、先読み（Scene・Plan は null。取り出した時点の内容で描く）
    private sealed record Job(long Frame, Scene? Scene, FramePlan? Plan, double Scale)
    {
        public TaskCompletionSource<RenderedFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
