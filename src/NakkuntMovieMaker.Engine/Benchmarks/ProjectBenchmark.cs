using System.Diagnostics;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Engine.Playback;
using NakkuntMovieMaker.Engine.Tachie;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Rendering.Vulkan;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.Engine.Benchmarks;

/// <summary>
/// プレビューの再生の結果。DroppedPercent は、表示する時刻までに描き終わらず飛ばしたフレームの割合。
/// PeakGpuMemoryBytes は GPU 版で確保したメモリの最大（CPU 版なら null）
/// </summary>
/// DroppedAfterFirstSecond は、再生を始めて最初の 1 秒（先に描いておくフレームがまだそろわない）より後に飛ばした数
public sealed record PlaybackBenchResult(
    long Shown, long Dropped, TimeSpan Elapsed, long PeakWorkingSetBytes, long? PeakGpuMemoryBytes = null, long DroppedAfterFirstSecond = 0)
{
    public double DroppedPercent => Shown + Dropped == 0 ? 0 : 100.0 * Dropped / (Shown + Dropped);
}

/// <summary>書き出しの結果。RealtimeFactor は実時間の何倍の速さか</summary>
public sealed record ExportBenchResult(long Frames, TimeSpan Elapsed, TimeSpan Duration, string? Encoder)
{
    public double RealtimeFactor => Duration.TotalSeconds / Elapsed.TotalSeconds;
}

/// <summary>
/// プロジェクトを使った性能の測定（M1 の手順 I-39）。プレビューの再生（音を出さない仮のデバイスの時計で、画面の更新の間隔で取り出す）、
/// シーンを繰り返して長くした書き出し、セリフ 1 行の合成から試聴まで、録音を止めてからタイムラインに置くまで
/// </summary>
public sealed class ProjectBenchmark(Project project, string projectFolder, RendererOptions? renderer = null)
{
    /// <summary>
    /// シーンを最初から最後まで再生し、表示できた・飛ばしたフレームを数える（scale はプレビューの解像度）。
    /// useProxies なら、プロジェクトの設定に合う動画のプロキシを先に作り（作ってあれば使い）、プレビューと同じくプロキシを読む
    /// </summary>
    public PlaybackBenchResult MeasurePlayback(double scale, bool useProxies = false, CancellationToken cancellationToken = default)
    {
        var scene = project.Root;
        var cache = new MediaCache(Path.Combine(projectFolder, ".nmm", "cache"));
        using var proxies = useProxies ? new ProxyQueue(cache) : null;
        if (proxies is not null)
        {
            var jobs = project.Assets.Values
                .Where(asset => asset.Kind == AssetKind.Video)
                .Select(asset => Path.GetFullPath(Path.Combine(projectFolder, asset.Path)))
                .Where(File.Exists)
                .Select(proxies.Enqueue)
                .ToList();
            foreach (var job in jobs)
            {
                job.Completion.GetAwaiter().GetResult();
            }
        }

        using var media = new ProjectMediaSource(project, projectFolder, cache, proxies: proxies);
        var timeline = new TimelineContext(project.Characters, new TachieLoader(projectFolder), new ProjectLipSync(media))
        {
            Color = FrameColor.From(project.Color),
        };
        using var backend = RenderBackendFactory.Create(renderer).Backend;
        using var frames = new FrameScheduler(scene, backend, media, timeline: timeline) { Scale = scale };

        // 準備（キャッシュの作成・デコーダーを開く）は測らない
        frames.RequestAsync(0, cancellationToken).GetAwaiter().GetResult();
        using var playback = new PlaybackController(scene, media, frames, new PlaybackOptions(NullAudioDevice: true));
        var watch = Stopwatch.StartNew();
        playback.Play();
        var peak = 0L;
        // 画面の更新（60Hz）と同じ間隔で取り出す。Windows の Thread.Sleep(16) は 31ms ほど眠ることがあり、
        // 60fps のシーンの半分のフレームを見ないまま数えてしまうので、時計で次の更新の時刻を決めて、少し手前から待つ
        var interval = TimeSpan.FromSeconds(1.0 / 60);
        var next = watch.Elapsed;
        using var timer = HighResolutionTimer.Begin();
        var firstSecond = (long)Math.Ceiling(scene.Fps.ToDouble());
        var lastDropped = -1L;
        var droppedLater = 0L;
        while (playback.IsPlaying && !cancellationToken.IsCancellationRequested)
        {
            if (!playback.TryGetFrame(out _) && playback.CurrentFrame is var current && current >= firstSecond && current != lastDropped)
            {
                lastDropped = current;
                droppedLater++;
            }

            peak = Math.Max(peak, Environment.WorkingSet); // Process.GetCurrentProcess() は 1 回で数 ms かかることがある
            next += interval;
            while (watch.Elapsed < next)
            {
                if (next - watch.Elapsed > TimeSpan.FromMilliseconds(3))
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(100);
                }
            }
        }

        var statistics = playback.Statistics;
        var gpu = (backend is FallbackBackend fallback ? fallback.Primary : backend) as VulkanBackend;
        return new PlaybackBenchResult(statistics.ShownFrames, statistics.DroppedFrames, watch.Elapsed, peak, gpu?.PeakGpuMemoryBytes, droppedLater);
    }

    /// <summary>シーンを repeat 回つなげた長さで書き出す（ハードウェアエンコーダーがあれば使う）</summary>
    public ExportBenchResult MeasureExport(int repeat, string outputPath, EncoderPreference encoder = EncoderPreference.Auto, CancellationToken cancellationToken = default)
    {
        var scene = project.Root;
        var duration = scene.Duration;
        var items = scene.Items.Values.ToList();
        var repeated = scene;
        for (var i = 1; i < repeat; i++)
        {
            foreach (var item in items)
            {
                var copy = item with { Id = ItemId.New(), Start = item.Start + duration * i, Group = null };
                repeated = repeated with { Items = repeated.Items.Add(copy.Id, copy) };
            }
        }

        var longProject = project.WithScene(repeated);
        using var pipeline = new ExportPipeline(longProject, longProject.RootScene, projectFolder, renderer);
        var result = pipeline.ExportVideoAsync(new ExportOptions(outputPath, Encoder: encoder), cancellationToken: cancellationToken).GetAwaiter().GetResult();
        return new ExportBenchResult(result.Frames, result.Elapsed, Core.Time.TimeMath.FrameToTimeSpan(result.Frames, scene.Fps), result.VideoEncoder);
    }

    /// <summary>
    /// セリフ 1 行を合成して、試聴できる（WAV がキャッシュにできる）までの時間（ミリ秒）。1 回目はエンジンの準備を含むので、続けて別の文で測る。
    /// 使える最初のエンジンを使い、なければ null
    /// </summary>
    public static (string Engine, double FirstMilliseconds, double AverageMilliseconds)? MeasureVoice(string cacheFolder, CancellationToken cancellationToken = default)
    {
        var registry = VoiceEngines.CreateDefault();
        using var engines = registry;
        var engine = engines.All.FirstOrDefault(candidate => candidate.Status.Available);
        if (engine is null)
        {
            return null;
        }

        using var service = new VoiceService(engines, new VoiceCache(cacheFolder));
        var spec = new VoiceSpec(engine.Id, engine.Voices.FirstOrDefault()?.Id ?? "", VoiceParams.Default);
        string[] lines = ["ゆっくりしていってね", "今日は動画の作り方を解説するよ", "最後まで見ていってくれよな", "それじゃあ始めよう", "よろしくお願いします"];
        var times = new List<double>();
        foreach (var line in lines)
        {
            var watch = Stopwatch.StartNew();
            service.GetAsync(spec, line + Guid.NewGuid().ToString("N")[..4], null, cancellationToken).GetAwaiter().GetResult();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        return (engine.Name, times[0], times.Skip(1).Average());
    }

    /// <summary>録音（音を返さない仮のデバイスで seconds 秒）を止めてから、タイムラインに置くまでの時間（ミリ秒）</summary>
    public double MeasureRecordingStop(double seconds, CancellationToken cancellationToken = default)
    {
        var path = Recording.NewPath(projectFolder, DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var input = AudioInput.Open(48000, 1, 48000 * 4, nullDevice: true);
        var recorder = Recorder.Start(input, path, 0);
        Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).GetAwaiter().GetResult();

        var watch = Stopwatch.StartNew();
        var result = recorder.Stop();
        recorder.Dispose();
        // 新しいレイヤーに置く（空いているレイヤーを探す代わり）
        var document = new ProjectDocument(project);
        document.Execute(EditCommands.AddLayer(project, project.RootScene, project.Root.Layers.Count, "録音"));
        var (operation, _) = Recording.AddToTimeline(document.Current, project.RootScene, document.Current.Root.Layers[^1].Id, result, projectFolder);
        document.Execute(operation);
        var elapsed = watch.Elapsed.TotalMilliseconds;
        File.Delete(path);
        return elapsed;
    }
}

/// <summary>
/// Windows のタイマーの細かさを 1ms にする（既定は 15.6ms で、Thread.Sleep(1) でも 15ms ほど眠るため、60Hz で取り出せない）。
/// 測定の間だけ使い、破棄すると元に戻す。ほかの OS では何もしない
/// </summary>
internal sealed partial class HighResolutionTimer : IDisposable
{
    private HighResolutionTimer()
    {
    }

    public static IDisposable Begin()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
        }

        return new HighResolutionTimer();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = TimeEndPeriod(1);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("winmm", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
