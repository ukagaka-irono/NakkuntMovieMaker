namespace NakkuntMovieMaker.Rendering;

/// <summary>
/// まず primary（GPU 版）で描き、描けなくなったら（デバイスが失われた・ドライバーの不具合など）、それ以降は fallback（CPU 版）で描く。
/// 切り替えたときは FellBack で理由を知らせる（一度だけ）。複数のスレッドから同時に呼んでよい
/// </summary>
public sealed class FallbackBackend(IRenderBackend primary, IRenderBackend fallback, Func<Exception, bool> isGpuFailure) : IRenderBackend
{
    private readonly IRenderBackend _primary = primary;
    private volatile IRenderBackend _current = primary;
    private int _fellBack;

    /// <summary>CPU 版に切り替えたとき（引数は理由。利用者に表示できる文）</summary>
    public event Action<string>? FellBack;

    public RenderBackendInfo Info => _current.Info;

    public bool HasFallenBack => Volatile.Read(ref _fellBack) != 0;

    /// <summary>まず使う描画（GPU 版）</summary>
    public IRenderBackend Primary => _primary;

    public RenderResult Render(FramePlan plan, IMediaSource media, RenderRequest request, CancellationToken cancellationToken = default)
    {
        var current = _current;
        if (ReferenceEquals(current, fallback))
        {
            return fallback.Render(plan, media, request, cancellationToken);
        }

        try
        {
            return current.Render(plan, media, request, cancellationToken);
        }
        catch (Exception ex) when (isGpuFailure(ex))
        {
            FallBack($"GPU で描けなくなったので、CPU で描きます（{ex.Message}）");
            return fallback.Render(plan, media, request, cancellationToken);
        }
    }

    public void Dispose()
    {
        _primary.Dispose();
        fallback.Dispose();
    }

    private void FallBack(string reason)
    {
        _current = fallback;
        if (Interlocked.Exchange(ref _fellBack, 1) == 0)
        {
            FellBack?.Invoke(reason);
        }
    }
}
