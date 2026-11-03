using System.CommandLine;
using System.Text;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Cli;

internal static class Program
{
    // export / render-frame / bench のサブコマンドは、エンジンの実装に合わせて追加する
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var root = new RootCommand("NakkuntMovieMaker のコマンドラインツール");
        root.Subcommands.Add(CreateProbeCommand());
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
