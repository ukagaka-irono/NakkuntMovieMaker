using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NakkuntMovieMaker.Media;

/// <summary>利用者が用意した ffmpeg の実行ファイルの情報。Encoders は、この書き出しで使えるソフトウェアエンコーダー（libx264・libx265）</summary>
public sealed record ExternalFfmpegInfo(string Path, string Version, IReadOnlyList<string> Encoders)
{
    /// <summary>その圧縮形式で使うエンコーダーの名前（H.264 は libx264、HEVC は libx265）</summary>
    public static string EncoderFor(VideoCodecKind codec) => codec == VideoCodecKind.H264 ? "libx264" : "libx265";

    public bool Supports(VideoCodecKind codec) => Encoders.Contains(EncoderFor(codec));
}

/// <summary>
/// 利用者が用意した ffmpeg の実行ファイル（x264・x265 を含むもの）。同梱の FFmpeg は LGPL の構成で x264 を含まないので、
/// x264 で書き出したい人は、自分で入手した ffmpeg を設定で指定する（GPL の ffmpeg はソフトに同梱せず、別のプロセスとして呼ぶだけ）
/// </summary>
public static partial class ExternalFfmpeg
{
    private static readonly string[] KnownEncoders = ["libx264", "libx265"];

    /// <summary>
    /// ffmpeg の版と、使えるエンコーダーを調べる
    /// </summary>
    /// <exception cref="MediaException">実行できない、または ffmpeg ではない</exception>
    public static ExternalFfmpegInfo Probe(string path)
    {
        var version = Run(path, ["-hide_banner", "-version"]);
        var match = VersionPattern().Match(version);
        if (!match.Success)
        {
            throw new MediaException($"ffmpeg ではないようです: {path}");
        }

        var encoders = Run(path, ["-hide_banner", "-encoders"]);
        var available = KnownEncoders.Where(name => Regex.IsMatch(encoders, $@"^\s*V\S*\s+{name}\s", RegexOptions.Multiline)).ToList();
        return new ExternalFfmpegInfo(path, match.Groups[1].Value, available);
    }

    // 実行して標準出力を返す。終了コードが 0 でなければ MediaException
    private static string Run(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new MediaException($"ffmpeg を実行できません: {path}");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill();
                throw new MediaException($"ffmpeg が応答しません: {path}");
            }

            return process.ExitCode == 0 ? output : throw new MediaException($"ffmpeg の実行に失敗しました: {LastLine(error.Result)}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new MediaException($"ffmpeg を実行できません: {path}（{ex.Message}）", ex);
        }
    }

    internal static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    [GeneratedRegex(@"^ffmpeg version (\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// 利用者が用意した ffmpeg で MP4 を書く。映像は ffmpeg の標準入力に BGRA のまま流して x264・x265 で圧縮し（一時ファイル）、
/// 音声は一時ファイルにためておき、最後に AAC にして映像とまとめる（映像はコピー、faststart）。
/// 色は MediaWriter と同じく BT.709・リミテッドレンジ
/// </summary>
public sealed class ExternalFfmpegWriter : IMediaFileWriter
{
    private readonly ExternalFfmpegInfo _ffmpeg;
    private readonly string _path;
    private readonly VideoEncoderSettings _video;
    private readonly AudioEncoderSettings? _audio;
    private readonly string _videoTemp;
    private readonly string _audioTemp;
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Task<string> _errors;
    private FileStream? _audioFile;
    private bool _finished;
    private bool _disposed;

    private ExternalFfmpegWriter(ExternalFfmpegInfo ffmpeg, string path, VideoEncoderSettings video, AudioEncoderSettings? audio)
    {
        _ffmpeg = ffmpeg;
        _path = path;
        _video = video;
        _audio = audio;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}";
        _videoTemp = Path.Combine(folder, name + ".video.mp4");
        _audioTemp = Path.Combine(folder, name + ".audio.f32");
        if (audio is not null)
        {
            _audioFile = new FileStream(_audioTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }

        _process = Start(ffmpeg.Path, VideoArguments(video, _videoTemp), redirectInput: true);
        _input = _process.StandardInput.BaseStream;
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public string? VideoEncoder => ExternalFfmpegInfo.EncoderFor(_video.Codec);

    /// <summary>
    /// 書き出しを始める
    /// </summary>
    /// <exception cref="MediaException">ffmpeg を実行できない、またはその圧縮形式のエンコーダーがない</exception>
    public static ExternalFfmpegWriter Create(ExternalFfmpegInfo ffmpeg, string path, VideoEncoderSettings video, AudioEncoderSettings? audio)
    {
        if (video.Width <= 0 || video.Height <= 0 || video.Width % 2 != 0 || video.Height % 2 != 0)
        {
            throw new ArgumentException("映像の幅と高さは、正の偶数にしてください", nameof(video));
        }

        if (!ffmpeg.Supports(video.Codec))
        {
            throw new MediaException($"外部の ffmpeg には {ExternalFfmpegInfo.EncoderFor(video.Codec)} が入っていません（{ffmpeg.Path}）");
        }

        try
        {
            return new ExternalFfmpegWriter(ffmpeg, path, video, audio);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new MediaException($"ffmpeg を実行できません: {ffmpeg.Path}（{ex.Message}）", ex);
        }
    }

    public void WriteVideo(BgraImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image.Width != _video.Width || image.Height != _video.Height)
        {
            throw new ArgumentException($"画像の大きさが違います（{image.Width}×{image.Height}、設定は {_video.Width}×{_video.Height}）", nameof(image));
        }

        try
        {
            _input.Write(image.Pixels);
        }
        catch (IOException ex)
        {
            // ffmpeg が途中で終わった（エラーの内容は ffmpeg の出力にある）
            throw Failed("映像の圧縮", ex);
        }
    }

    public void WriteAudio(ReadOnlySpan<float> stereo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioFile is null)
        {
            throw new InvalidOperationException("音声なしで作った書き出しです");
        }

        _audioFile.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(stereo));
    }

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        _input.Close();
        _process.WaitForExit();
        if (_process.ExitCode != 0)
        {
            throw Failed("映像の圧縮", null);
        }

        _audioFile?.Dispose();
        _audioFile = null;

        // 映像をコピーし、音声を AAC にしてまとめる
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", _videoTemp];
        if (_audio is { } audio)
        {
            arguments.AddRange(["-f", "f32le", "-ar", audio.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", _audioTemp]);
            arguments.AddRange(["-map", "0:v", "-map", "1:a", "-c:a", "aac", "-b:a", $"{audio.BitrateKbps}k"]);
        }

        arguments.AddRange(["-c:v", "copy", "-movflags", "+faststart", _path]);
        using var mux = Start(_ffmpeg.Path, arguments, redirectInput: false);
        var errors = mux.StandardError.ReadToEndAsync();
        mux.WaitForExit();
        if (mux.ExitCode != 0)
        {
            throw new MediaException($"映像と音声をまとめられませんでした（ffmpeg: {ExternalFfmpeg.LastLine(errors.Result)}）");
        }

        _finished = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
        }

        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit();
        }

        _process.Dispose();
        _audioFile?.Dispose();
        File.Delete(_videoTemp);
        File.Delete(_audioTemp);
    }

    private MediaException Failed(string what, Exception? inner)
    {
        if (!_process.HasExited)
        {
            _process.Kill();
        }

        _process.WaitForExit();
        var message = ExternalFfmpeg.LastLine(_errors.Result);
        return new MediaException($"{what}に失敗しました（ffmpeg: {(message.Length > 0 ? message : $"終了コード {_process.ExitCode}")}）", inner);
    }

    // BGRA の生の画像を標準入力から受け取り、x264・x265 で映像だけの MP4 にする
    private static List<string> VideoArguments(VideoEncoderSettings video, string output)
    {
        var rate = $"{video.FrameRate.Numerator}/{video.FrameRate.Denominator}";
        var gop = (int)Math.Max(1, Math.Round(video.FrameRate.ToDouble() * 2));
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{video.Width}x{video.Height}", "-framerate", rate, "-i", "pipe:0",
            "-vf", "scale=out_color_matrix=bt709:out_range=tv:flags=bilinear+accurate_rnd+full_chroma_int,format=yuv420p",
            "-c:v", ExternalFfmpegInfo.EncoderFor(video.Codec), "-preset", "medium",
            "-b:v", $"{video.BitrateKbps}k", "-maxrate", $"{video.BitrateKbps * 2}k", "-bufsize", $"{video.BitrateKbps * 2}k",
            "-g", gop.ToString(CultureInfo.InvariantCulture),
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
        ];
        if (video.Codec == VideoCodecKind.Hevc)
        {
            // QuickTime などで再生できるように hvc1 にする。x265 の進み具合の表示は消す
            arguments.AddRange(["-tag:v", "hvc1", "-x265-params", "log-level=error"]);
        }

        arguments.AddRange(["-an", output]);
        return arguments;
    }

    private static Process Start(string path, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new MediaException($"ffmpeg を実行できません: {path}");
    }
}
