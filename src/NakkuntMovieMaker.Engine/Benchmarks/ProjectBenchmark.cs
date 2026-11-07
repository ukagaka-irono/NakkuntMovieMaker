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
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.Engine.Benchmarks;

/// <summary>プレビューの再生の結果。DroppedPercent は、表示する時刻までに描き終わらず飛ばしたフレームの割合</summary>
public sealed record PlaybackBenchResult(long Shown, long Dropped, TimeSpan Elapsed, long PeakWorkingSetBytes)
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
    /// <summary>シーンを最初から最後まで再生し、表示できた・飛ばしたフレームを数える（scale はプレビューの解像度）</summary>
    public PlaybackBenchResult MeasurePlayback(double scale, CancellationToken cancellationToken = default)
    {
        var scene = project.Root;
        using var media = new ProjectMediaSource(project, projectFolder, new MediaCache(Path.Combine(projectFolder, ".nmm", "cache")));
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
        while (playback.IsPlaying && !cancellationToken.IsCancellationRequested)
        {
            playback.TryGetFrame(out _);
            peak = Math.Max(peak, Process.GetCurrentProcess().WorkingSet64);
            Thread.Sleep(16);
        }

        var statistics = playback.Statistics;
        return new PlaybackBenchResult(statistics.ShownFrames, statistics.DroppedFrames, watch.Elapsed, peak);
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
