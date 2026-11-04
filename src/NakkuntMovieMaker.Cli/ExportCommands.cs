using System.CommandLine;
using System.Globalization;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Engine.Subtitles;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Cli;

/// <summary>nmm export と nmm render-frame</summary>
internal static class ExportCommands
{
    private static readonly Argument<FileInfo> ProjectArgument = new("project") { Description = "プロジェクト（.nmmproj）" };

    public static Command CreateExportCommand()
    {
        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "書き出すファイル。拡張子が .wav なら音声だけを書き出す",
            Required = true,
        };
        var presetOption = new Option<string?>("--preset")
        {
            Description = $"プリセット（{string.Join("・", ExportPresets.All.Select(preset => preset.Id))}。省略すると scene）",
        };
        var codecOption = new Option<string?>("--codec") { Description = "映像のコーデック（h264・hevc）" };
        var widthOption = new Option<int?>("--width") { Description = "出力の幅（縦横比が違えば、黒い帯を付けて中央に置く）" };
        var heightOption = new Option<int?>("--height") { Description = "出力の高さ" };
        var bitrateOption = new Option<int?>("--bitrate") { Description = "映像のビットレート（kbps）" };
        var audioBitrateOption = new Option<int?>("--audio-bitrate") { Description = "音声のビットレート（kbps）" };
        var fromOption = new Option<string?>("--from") { Description = "書き出す範囲の始まり（フレーム番号、または 1.5s・00:01:02.5 のような時刻）" };
        var toOption = new Option<string?>("--to") { Description = "書き出す範囲の終わり（このフレームは含めない）" };
        var softwareOption = new Option<bool>("--software") { Description = "ハードウェアエンコーダーを使わない" };
        var ffmpegOption = new Option<FileInfo?>("--ffmpeg")
        {
            Description = "外部の ffmpeg（自分で入手したもの）の実行ファイル。指定すると、それで書き出す（H.264 は x264、HEVC は x265）",
        };
        var loudnessOption = new Option<double?>("--loudness") { Description = "音全体のラウドネスをそろえる値（LUFS。YouTube なら -14）" };
        var subtitlesOption = new Option<FileInfo?>("--subtitles") { Description = "セリフの字幕を SRT にも書き出す" };
        var sceneOption = SceneOption();
        var workersOption = new Option<int>("--workers") { Description = "並列に描くスレッドの数（0 なら自動）" };

        var command = new Command("export", "シーンを動画（MP4）か音声（WAV）に書き出す");
        command.Arguments.Add(ProjectArgument);
        foreach (var option in new Option[]
                 {
                     outputOption, presetOption, codecOption, widthOption, heightOption, bitrateOption, audioBitrateOption, fromOption, toOption,
                     softwareOption, ffmpegOption, loudnessOption, subtitlesOption, sceneOption, workersOption,
                 })
        {
            command.Options.Add(option);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!TryOpen(parseResult.GetValue(ProjectArgument)!, parseResult.GetValue(sceneOption), out var project, out var scene, out var folder))
            {
                return 2;
            }

            var output = parseResult.GetValue(outputOption)!.FullName;
            if (!TryParsePosition(parseResult.GetValue(fromOption), scene.Fps, out var from) || !TryParsePosition(parseResult.GetValue(toOption), scene.Fps, out var to))
            {
                Console.Error.WriteLine("--from・--to は、フレーム番号か時刻（1.5s・00:01:02.5 など）で指定してください。");
                return 2;
            }

            var presetId = parseResult.GetValue(presetOption) ?? ExportPresets.SceneSize.Id;
            var preset = ExportPresets.All.FirstOrDefault(candidate => candidate.Id == presetId);
            if (preset is null)
            {
                Console.Error.WriteLine($"プリセットが見つかりません: {presetId}（{string.Join("・", ExportPresets.All.Select(candidate => candidate.Id))}）");
                return 2;
            }

            var codec = preset.Codec;
            switch (parseResult.GetValue(codecOption)?.ToLowerInvariant())
            {
                case null:
                    break;
                case "h264" or "h.264" or "avc":
                    codec = VideoCodecKind.H264;
                    break;
                case "hevc" or "h265" or "h.265":
                    codec = VideoCodecKind.Hevc;
                    break;
                case var other:
                    Console.Error.WriteLine($"コーデックが分かりません: {other}（h264・hevc）");
                    return 2;
            }

            using var pipeline = new ExportPipeline(project, scene.Id, folder);
            try
            {
                var loudness = parseResult.GetValue(loudnessOption);
                ExportResult result;
                if (Path.GetExtension(output).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    result = await pipeline.ExportWavAsync(output, from, to, loudness, cancellationToken: cancellationToken);
                }
                else
                {
                    var ffmpeg = parseResult.GetValue(ffmpegOption) is { } ffmpegFile ? ExternalFfmpeg.Probe(ffmpegFile.FullName) : null;
                    var options = ExportOptions.FromPreset(output, preset) with
                    {
                        ExternalFfmpeg = ffmpeg,
                        Codec = codec,
                        From = from,
                        To = to,
                        Encoder = parseResult.GetValue(softwareOption) ? EncoderPreference.Software : EncoderPreference.Auto,
                        LoudnessTarget = loudness,
                        Workers = parseResult.GetValue(workersOption),
                    };
                    options = options with
                    {
                        Width = parseResult.GetValue(widthOption) ?? options.Width,
                        Height = parseResult.GetValue(heightOption) ?? options.Height,
                        VideoBitrateKbps = parseResult.GetValue(bitrateOption) ?? options.VideoBitrateKbps,
                        AudioBitrateKbps = parseResult.GetValue(audioBitrateOption) ?? options.AudioBitrateKbps,
                    };
                    result = await pipeline.ExportVideoAsync(options, new ConsoleProgress(), cancellationToken);
                    Console.Error.WriteLine();
                }

                if (parseResult.GetValue(subtitlesOption) is { } subtitles)
                {
                    Srt.WriteFile(subtitles.FullName, SubtitleFiles.Export(scene, from: from, to: to));
                }

                var encoder = result.VideoEncoder is null ? "" : $"、エンコーダー {result.VideoEncoder}";
                Console.WriteLine($"{result.Path} に書き出しました（{result.Frames} フレーム{encoder}、{result.Elapsed.TotalSeconds:F1} 秒）");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("書き出しを中止しました。");
                return 130;
            }
            catch (Exception ex) when (ex is MediaException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });
        return command;
    }

    public static Command CreateRenderFrameCommand()
    {
        var outputOption = new Option<FileInfo>("--output", "-o") { Description = "書き出す画像（.png・.jpg・.webp）", Required = true };
        var frameOption = new Option<string>("--frame", "-f") { Description = "フレーム番号、または時刻（1.5s など）", DefaultValueFactory = _ => "0" };
        var widthOption = new Option<int?>("--width") { Description = "画像の幅（省略するとシーンの大きさ）" };
        var heightOption = new Option<int?>("--height") { Description = "画像の高さ" };
        var sceneOption = SceneOption();

        var command = new Command("render-frame", "シーンの 1 フレームを画像に書き出す");
        command.Arguments.Add(ProjectArgument);
        foreach (var option in new Option[] { outputOption, frameOption, widthOption, heightOption, sceneOption })
        {
            command.Options.Add(option);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!TryOpen(parseResult.GetValue(ProjectArgument)!, parseResult.GetValue(sceneOption), out var project, out var scene, out var folder))
            {
                return 2;
            }

            if (!TryParsePosition(parseResult.GetValue(frameOption), scene.Fps, out var frame) || frame is null)
            {
                Console.Error.WriteLine("--frame は、フレーム番号か時刻（1.5s・00:01:02.5 など）で指定してください。");
                return 2;
            }

            var output = parseResult.GetValue(outputOption)!.FullName;
            using var pipeline = new ExportPipeline(project, scene.Id, folder);
            try
            {
                await pipeline.ExportStillAsync(output, frame.Value, parseResult.GetValue(widthOption), parseResult.GetValue(heightOption), cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            Console.WriteLine($"{output} に書き出しました（{frame} フレーム目）");
            return 0;
        });
        return command;
    }

    private static Option<string?> SceneOption() => new("--scene") { Description = "書き出すシーンの名前（省略するとメインのシーン）" };

    // プロジェクトを読み、書き出すシーンを選ぶ
    private static bool TryOpen(FileInfo entry, string? sceneName, out Project project, out Scene scene, out string folder)
    {
        project = null!;
        scene = null!;
        folder = entry.DirectoryName ?? ".";
        try
        {
            project = new ProjectStore().Load(entry.FullName);
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"プロジェクトを開けません: {ex.Message}");
            return false;
        }

        if (sceneName is null)
        {
            scene = project.Root;
        }
        else if (project.Scenes.Values.FirstOrDefault(candidate => candidate.Name == sceneName) is { } found)
        {
            scene = found;
        }
        else
        {
            Console.Error.WriteLine($"シーンが見つかりません: {sceneName}（{string.Join("・", project.Scenes.Values.Select(candidate => candidate.Name))}）");
            return false;
        }

        foreach (var missing in Core.Assets.AssetLinks.FindMissing(project, folder))
        {
            Console.Error.WriteLine($"警告: 素材が見つかりません: {missing.Path}");
        }

        return true;
    }

    /// <summary>位置の指定を読む。整数はフレーム番号、「1.5s」は秒、「00:01:02.5」「01:02.5」は時刻（最も近いフレームに合わせる）</summary>
    internal static bool TryParsePosition(string? text, Rational fps, out long? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        text = text.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            frame = number;
            return true;
        }

        if (!TryParseSeconds(text, out var seconds))
        {
            return false;
        }

        frame = TimeMath.TimeSpanToFrame(TimeSpan.FromSeconds(seconds), fps, Rounding.Nearest);
        return true;
    }

    private static bool TryParseSeconds(string text, out double seconds)
    {
        seconds = 0;
        if (text.EndsWith('s'))
        {
            return double.TryParse(text[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds);
        }

        if (!text.Contains(':') || text.Split(':').Length > 3)
        {
            return false;
        }

        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            seconds = seconds * 60 + value;
        }

        return true;
    }

    // 進み具合を標準エラーの 1 行に上書きして表示する
    private sealed class ConsoleProgress : IProgress<ExportProgress>
    {
        private long _lastPercent = -1;

        public void Report(ExportProgress value)
        {
            var percent = (long)(value.Fraction * 100);
            if (percent == _lastPercent)
            {
                return;
            }

            _lastPercent = percent;
            var speed = value.Elapsed.TotalSeconds > 0 ? value.Frame / value.Elapsed.TotalSeconds : 0;
            Console.Error.Write($"\r書き出し中 {percent,3}%（{value.Frame}/{value.TotalFrames} フレーム、{speed:F1}fps）");
        }
    }
}
