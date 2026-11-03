using System.CommandLine;
using System.Text;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Engine.Benchmarks;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var root = new RootCommand("NakkuntMovieMaker のコマンドラインツール");
        root.Subcommands.Add(ExportCommands.CreateExportCommand());
        root.Subcommands.Add(ExportCommands.CreateRenderFrameCommand());
        root.Subcommands.Add(CreateProbeCommand());
        root.Subcommands.Add(CreateBenchCommand());
        return root.Parse(args).Invoke();
    }

    private static Command CreateProbeCommand()
    {
        var versionOption = new Option<bool>("--version")
        {
            Description = "読み込んだ FFmpeg と nmm_audio のバージョン・ライセンスを表示する",
        };

        var fileArgument = new Argument<FileInfo?>("file")
        {
            Description = "情報を表示するメディアファイル",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("probe", "FFmpeg・nmm_audio やメディアファイルの情報を表示する");
        command.Options.Add(versionOption);
        command.Arguments.Add(fileArgument);
        command.SetAction(parseResult =>
        {
            if (parseResult.GetValue(versionOption))
            {
                return PrintNativeVersions();
            }

            var file = parseResult.GetValue(fileArgument);
            if (file is null)
            {
                Console.Error.WriteLine("メディアファイルを指定するか、--version を指定してください。");
                return 2;
            }

            return PrintMediaInfo(file.FullName);
        });
        return command;
    }

    private static Command CreateBenchCommand()
    {
        var videoOption = new Option<FileInfo?>("--video") { Description = "動画のレイヤーに使う動画（1080p の H.264 を想定）" };
        var framesOption = new Option<int>("--frames") { Description = "場面ごとに描く枚数", DefaultValueFactory = _ => 120 };
        var workersOption = new Option<int>("--workers") { Description = "並列に描くスレッドの数（1 と、この数の両方で測る）", DefaultValueFactory = _ => 3 };
        var folderOption = new Option<DirectoryInfo?>("--folder") { Description = "素材とキャッシュを置く作業フォルダ（省略すると一時フォルダ）" };
        var onlyOption = new Option<string[]>("--only")
        {
            Description = $"測る場面だけを指定する（{string.Join("・", RenderBenchmark.Scenarios.Select(scenario => scenario.Name))}・seek）",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("bench", "描画の速さを測る（1080p・30fps・8 レイヤーのシーン）");
        command.Options.Add(videoOption);
        command.Options.Add(framesOption);
        command.Options.Add(workersOption);
        command.Options.Add(folderOption);
        command.Options.Add(onlyOption);
        command.SetAction(parseResult =>
        {
            var folder = parseResult.GetValue(folderOption)?.FullName ?? Path.Combine(Path.GetTempPath(), "nmm-bench");
            var video = parseResult.GetValue(videoOption);
            if (video is { Exists: false })
            {
                Console.Error.WriteLine($"動画が見つかりません: {video.FullName}");
                return 2;
            }

            var frames = Math.Max(10, parseResult.GetValue(framesOption));
            var workers = Math.Max(1, parseResult.GetValue(workersOption));
            var only = parseResult.GetValue(onlyOption) is { Length: > 0 } names ? names.ToHashSet(StringComparer.Ordinal) : null;
            var benchmark = new RenderBenchmark(folder, video?.FullName);
            Console.WriteLine($"CPU の論理コア数 {Environment.ProcessorCount}、動画 {(benchmark.HasVideo ? Path.GetFileName(video!.FullName) : "なし")}、{frames} 枚ずつ");
            Console.WriteLine("場面                                      スレッド  平均(ms)  95%(ms)  最大(ms)      fps");
            foreach (var scenario in RenderBenchmark.Scenarios.Where(scenario => only is null || only.Contains(scenario.Name)))
            {
                foreach (var count in workers == 1 ? new[] { 1 } : [1, workers])
                {
                    var result = benchmark.Run(scenario, frames, count);
                    Console.WriteLine($"{scenario.Description,-36}{count,6}{result.MeanMilliseconds,10:F1}{result.P95Milliseconds,9:F1}{result.MaxMilliseconds,10:F1}{result.FramesPerSecond,9:F1}");
                }
            }

            if (only is not null && !only.Contains("seek"))
            {
                return 0;
            }

            var (imagesOnly, withVideo) = benchmark.MeasureSeek(30);
            Console.WriteLine($"止まっているときの表示（1/2 解像度・ばらばらの位置）：画像だけ {imagesOnly:F1}ms、動画あり {(double.IsNaN(withVideo) ? "-" : $"{withVideo:F1}ms")}");
            return 0;
        });
        return command;
    }

    private static int PrintMediaInfo(string path)
    {
        MediaInfo info;
        try
        {
            info = MediaProbe.Probe(path);
        }
        catch (Exception ex) when (ex is MediaException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"{Path.GetFileName(path)}（{info.Kind}、形式 {info.FormatName}）");
        if (info.Duration is { } duration)
        {
            Console.WriteLine($"  長さ: {duration.ToDouble():0.###} 秒");
        }

        foreach (var video in info.VideoStreams)
        {
            var fps = video.FrameRate is { } rate ? $"{rate.ToDouble():0.###}fps（{rate}）" : "フレームレート不明";
            var rotation = video.Rotation != 0 ? $"、{video.Rotation} 度回転" : "";
            Console.WriteLine($"  映像 #{video.Index}: {video.Codec} {video.Width}×{video.Height} {fps} {video.PixelFormat}{rotation}");
        }

        foreach (var audio in info.AudioStreams)
        {
            Console.WriteLine($"  音声 #{audio.Index}: {audio.Codec} {audio.SampleRate}Hz {audio.Channels}ch");
        }

        return 0;
    }

    private static int PrintNativeVersions()
    {
        var exitCode = 0;

        if (FfmpegLibrary.TryLoad(out var ffmpeg, out var ffmpegError))
        {
            Console.WriteLine($"FFmpeg {ffmpeg!.Version}");
            Console.WriteLine($"  avcodec {ffmpeg.AvcodecVersion} / avformat {ffmpeg.AvformatVersion} / avutil {ffmpeg.AvutilVersion}");
            Console.WriteLine($"  ライセンス: {ffmpeg.License}");
            Console.WriteLine($"  構成: {ffmpeg.Configuration}");
            if (!ffmpeg.IsLgpl)
            {
                Console.Error.WriteLine("警告: LGPL ではない構成の FFmpeg です。このまま配布物に同梱しないでください。");
            }
        }
        else
        {
            Console.Error.WriteLine($"FFmpeg: {ffmpegError}");
            exitCode = 1;
        }

        if (NativeAudio.TryGetInfo(out var audio, out var audioError))
        {
            Console.WriteLine($"miniaudio {audio!.MiniaudioVersion}（nmm_audio API {audio.ApiVersion}）");
        }
        else
        {
            Console.Error.WriteLine($"nmm_audio: {audioError}");
            exitCode = 1;
        }

        return exitCode;
    }
}
