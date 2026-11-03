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

        var command = new Command("probe", "FFmpeg・nmm_audio やメディアファイルの情報を表示する");
        command.Options.Add(versionOption);
        command.SetAction(parseResult =>
        {
            if (!parseResult.GetValue(versionOption))
            {
                Console.Error.WriteLine("メディアファイルの情報の表示は、まだ実装していません。--version を指定してください。");
                return 2;
            }

            return PrintNativeVersions();
        });
        return command;
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
