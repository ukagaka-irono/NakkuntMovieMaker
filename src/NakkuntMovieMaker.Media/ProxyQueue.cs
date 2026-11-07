namespace NakkuntMovieMaker.Media;

/// <summary>プロキシを作る仕事の状態</summary>
public enum ProxyJobState
{
    /// <summary>順番を待っている</summary>
    Queued,

    /// <summary>作っている</summary>
    Running,

    /// <summary>できた（ProxyPath にある）</summary>
    Completed,

    /// <summary>作れなかった（Error に理由）</summary>
    Failed,

    /// <summary>キャンセルした</summary>
    Canceled,
}

/// <summary>
/// プロキシを作る 1 つの仕事。Changed は状態か進み具合が変わったときに、作業しているスレッドで呼ばれる
/// （画面に出すときは UI のスレッドに移すこと）
/// </summary>
public sealed class ProxyJob
{
    // 進み具合は、これだけ進んだときだけ知らせる（フレームごとに知らせると多すぎる）
    private const double ProgressStep = 0.01;

    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double _reportedProgress;
    private volatile ProxyJobState _state;
    private double _progress;

    internal ProxyJob(string sourcePath, CancellationToken queueToken)
    {
        SourcePath = sourcePath;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(queueToken);
        Token = _cancellation.Token;
    }

    /// <summary>素材の場所（フルパス）</summary>
    public string SourcePath { get; }

    public ProxyJobState State => _state;

    /// <summary>進み具合（0〜1）</summary>
    public double Progress => Volatile.Read(ref _progress);

    /// <summary>できたプロキシの場所（できるまでは null）</summary>
    public string? ProxyPath { get; private set; }

    /// <summary>作れなかった理由（MediaException の Message は利用者に表示できる）</summary>
    public Exception? Error { get; private set; }

    /// <summary>終わったら、プロキシの場所を返す。失敗したら例外、キャンセルしたら OperationCanceledException</summary>
    public Task<string> Completion => _completion.Task;

    public bool IsFinished => _state is ProxyJobState.Completed or ProxyJobState.Failed or ProxyJobState.Canceled;

    public event EventHandler? Changed;

    /// <summary>やめる。作りかけのファイルは消す</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // もう終わっている
        }
    }

    internal CancellationToken Token { get; }

    internal void Start()
    {
        _state = ProxyJobState.Running;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ReportProgress(double value)
    {
        Volatile.Write(ref _progress, value);
        if (value - _reportedProgress >= ProgressStep || (value >= 1 && _reportedProgress < 1))
        {
            _reportedProgress = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void Complete(string path)
    {
        ProxyPath = path;
        Volatile.Write(ref _progress, 1);
        Finish(ProxyJobState.Completed);
        _completion.TrySetResult(path);
    }

    internal void Fail(Exception error)
    {
        Error = error;
        Finish(ProxyJobState.Failed);
        _completion.TrySetException(error);
    }

    internal void SetCanceled()
    {
        Finish(ProxyJobState.Canceled);
        _completion.TrySetCanceled(Token);
    }

    private void Finish(ProxyJobState state)
    {
        _state = state;
        _cancellation.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ProgressReporter(ProxyJob job) : IProgress<double>
    {
        public void Report(double value) => job.ReportProgress(value);
    }

    internal IProgress<double> CreateProgress() => new ProgressReporter(this);
}

/// <summary>
/// プロキシを後ろで順に作る。同じ素材を何度入れても、待っている・作っている仕事があればそれを返す。
/// 作ったプロキシは MediaCache（proxies）に置く。破棄すると、すべての仕事をキャンセルし、終わるまで待つ。
/// 複数のスレッドから使ってよい
/// </summary>
public sealed class ProxyQueue : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly MediaCache _cache;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Dictionary<string, (ProxyJob Job, Task Worker)> _active = new(PathComparer);
    private readonly Lock _lock = new();

    /// <param name="maxConcurrency">同時に作る数（エンコーダーを取り合うので、既定は 1）</param>
    public ProxyQueue(MediaCache cache, ProxySettings? settings = null, int maxConcurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _cache = cache;
        Settings = settings ?? ProxySettings.Default;
        _slots = new SemaphoreSlim(maxConcurrency);
    }

    public ProxySettings Settings { get; }

    /// <summary>仕事が加わったか、状態か進み具合が変わった（作業しているスレッドで呼ばれる）</summary>
    public event EventHandler<ProxyJob>? JobChanged;

    /// <summary>待っている・作っている仕事</summary>
    public IReadOnlyList<ProxyJob> ActiveJobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _active.Values.Select(entry => entry.Job)];
            }
        }
    }

    /// <summary>素材のプロキシがもう作ってあれば、その場所。なければ null</summary>
    public string? FindProxy(string sourcePath) => _cache.FindProxy(sourcePath, Settings);

    /// <summary>素材のプロキシを作る仕事を加える。同じ素材の仕事が待っている・作っているなら、それを返す</summary>
    public ProxyJob Enqueue(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        ProxyJob job;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            if (_active.TryGetValue(fullPath, out var existing))
            {
                return existing.Job;
            }

            job = new ProxyJob(fullPath, _disposed.Token);
            job.Changed += OnJobChanged;
            _active[fullPath] = (job, Task.Run(() => RunAsync(job)));
        }

        JobChanged?.Invoke(this, job);
        return job;
    }

    /// <summary>素材のプロキシを消す（作っている途中ならキャンセルする）。消したら true</summary>
    /// <exception cref="IOException">プロキシを開いている</exception>
    public bool Delete(string sourcePath)
    {
        Cancel(sourcePath);
        return _cache.DeleteProxy(sourcePath, Settings);
    }

    /// <summary>素材の仕事が待っている・作っているならキャンセルする</summary>
    public void Cancel(string sourcePath)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(Path.GetFullPath(sourcePath), out var entry))
            {
                entry.Job.Cancel();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested)
        {
            return;
        }

        Task[] workers;
        lock (_lock)
        {
            _disposed.Cancel();
            workers = [.. _active.Values.Select(entry => entry.Worker)];
        }

        // 作りかけのファイルを消し終えるまで待つ（RunAsync は例外を投げない）
        Task.WaitAll(workers);
        _disposed.Dispose();
        _slots.Dispose();
    }

    private async Task RunAsync(ProxyJob job)
    {
        try
        {
            await _slots.WaitAsync(job.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(job, () => job.SetCanceled());
            return;
        }

        try
        {
            job.Start();
            var path = _cache.GetProxy(job.SourcePath, Settings, job.CreateProgress(), job.Token);
            Finish(job, () => job.Complete(path));
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            Finish(job, () => job.SetCanceled());
        }
        catch (Exception error)
        {
            Finish(job, () => job.Fail(error));
        }
        finally
        {
            _slots.Release();
        }
    }

    // 仕事を一覧から外してから、終わったことを知らせる（知らせを受けて同じ素材を入れ直せるように）
    private void Finish(ProxyJob job, Action complete)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(job.SourcePath, out var entry) && entry.Job == job)
            {
                _active.Remove(job.SourcePath);
            }
        }

        complete();
    }

    private void OnJobChanged(object? sender, EventArgs e) => JobChanged?.Invoke(this, (ProxyJob)sender!);
}
