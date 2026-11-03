using System.Collections.Concurrent;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Voice;

/// <summary>声の用意ができた結果。CacheKey はセリフのアイテムに保存するキー、Duration は声の長さ</summary>
public sealed record VoiceResult(string CacheKey, string Path, string Reading, TimeSpan Duration, bool FromCache);

/// <summary>
/// セリフの声を用意する。キャッシュにあればそれを使い、なければエンジンで合成してキャッシュに置く。
/// エンジンはスレッドセーフとは限らないので、エンジンごとに 1 本のスレッドで、依頼を順番に処理する
/// </summary>
public sealed class VoiceService : IDisposable
{
    private readonly VoiceEngineRegistry _engines;
    private readonly VoiceCache _cache;
    private readonly ConcurrentDictionary<string, EngineWorker> _workers = new(StringComparer.Ordinal);

    public VoiceService(VoiceEngineRegistry engines, VoiceCache cache)
    {
        _engines = engines;
        _cache = cache;
    }

    public VoiceCache Cache => _cache;

    /// <summary>
    /// voice の声で text を読ませた声を用意する。reading を指定すると、その読みで合成する（null なら、エンジンが本文から作る）
    /// </summary>
    /// <exception cref="VoiceEngineException">エンジンがない・使えない、または合成できなかった</exception>
    public Task<VoiceResult> GetAsync(VoiceSpec voice, string text, string? reading, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(text);
        if (_engines.Find(voice.EngineId) is not { } engine)
        {
            return Task.FromException<VoiceResult>(new VoiceEngineException($"音声合成エンジン「{voice.EngineId}」がありません"));
        }

        if (engine.Status is { Available: false } status)
        {
            return Task.FromException<VoiceResult>(new VoiceEngineException(status.Message ?? $"{engine.Name} は使えません"));
        }

        var worker = _workers.GetOrAdd(engine.Id, _ => new EngineWorker(engine));
        return worker.Run(() =>
        {
            var request = new VoiceRequest(text, reading, voice.VoiceId, voice.Params);
            var actualReading = reading ?? engine.MakeReading(text) ?? "";
            var key = VoiceCache.Key(engine, request, actualReading);
            if (_cache.Load(key) is { } cached)
            {
                return new VoiceResult(key, _cache.PathOf(key), actualReading, cached.Duration, FromCache: true);
            }

            var audio = engine.Synthesize(request with { Reading = actualReading.Length > 0 ? actualReading : null }, cancellationToken);
            _cache.Save(key, audio);
            return new VoiceResult(key, _cache.PathOf(key), actualReading, audio.Duration, FromCache: false);
        }, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var worker in _workers.Values)
        {
            worker.Dispose();
        }
    }

    // エンジン 1 つぶんの作業用のスレッド。依頼を受け付けた順に処理する
    private sealed class EngineWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public EngineWorker(IVoiceEngine engine)
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = $"音声合成（{engine.Name}）",
            };

            if (engine.RequiresSingleThreadedApartment && OperatingSystem.IsWindows())
            {
                _thread.SetApartmentState(ApartmentState.STA);
            }

            _thread.Start();
        }

        public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work());
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex) when (ex is VoiceEngineException or IOException or InvalidDataException)
                {
                    completion.TrySetException(ex);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(new VoiceEngineException($"音声合成に失敗しました（{ex.Message}）", ex));
                }
            });
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }
    }
}
