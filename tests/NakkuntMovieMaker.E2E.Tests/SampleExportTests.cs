using System.ComponentModel;
using System.Diagnostics;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Cli;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using SkiaSharp;

namespace NakkuntMovieMaker.E2E.Tests;

/// <summary>
/// サンプルを nmm export で書き出し、出来上がった動画を確かめる：形式（H.264・大きさ・30fps・yuv420p・AAC 48kHz）、
/// フレーム数がちょうど、長さの誤差が 1 フレーム以内、全フレームをデコードできる、render-frame の画像と PSNR 38dB 以上で一致、
/// セリフの声が置いた位置から 1 フレーム以内で鳴る。PATH に ffprobe・ffmpeg があれば、色の情報（BT.709）とデコードのエラーも確かめる
/// </summary>
public sealed class SampleExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-e2e", Guid.NewGuid().ToString("N"));

    public SampleExportTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("general", "youtube-1080p", 1920, 1080)]
    [InlineData("yukkuri", "youtube-1080p", 1920, 1080)]
    [InlineData("yukkuri", "vertical-1080x1920", 1080, 1920)]
    public void ExportsWithPresets(string sample, string preset, int width, int height)
    {
        Samples.RequireFfmpeg();
        var entry = Samples.CopyToTemp(sample, _folder);
        var output = Path.Combine(_folder, $"{sample}-{preset}.mp4");

        Assert.Equal(0, Program.Main(["export", entry, "-o", output, "--preset", preset]));

        var scene = new ProjectStore().Load(entry).Root;
        AssertVideo(output, width, height, scene.Duration);
        AssertWithExternalTools(output);
    }

    [Theory]
    [InlineData("general")]
    [InlineData("yukkuri")]
    public void ExportedFramesMatchRenderFrame(string sample)
    {
        Samples.RequireFfmpeg();
        var entry = Samples.CopyToTemp(sample, _folder);
        var output = Path.Combine(_folder, $"{sample}.mp4");
        Assert.Equal(0, Program.Main(["export", entry, "-o", output]));
        var scene = new ProjectStore().Load(entry).Root;

        foreach (var frame in new[] { 0, scene.Duration / 3, scene.Duration * 2 / 3, scene.Duration - 1 })
        {
            var expected = Path.Combine(_folder, $"{sample}-{frame}.png");
            Assert.Equal(0, Program.Main(["render-frame", entry, "-f", frame.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", expected]));
            var psnr = Psnr(LoadPng(expected), DecodeFrame(output, frame, scene.Fps));
            Assert.True(psnr >= 38, $"{sample} の {frame} フレーム目の PSNR が {psnr:0.0}dB");
        }
    }

    [Fact]
    public void ExportsOnlyTheRange()
    {
        Samples.RequireFfmpeg();
        var entry = Samples.CopyToTemp("yukkuri", _folder);
        var output = Path.Combine(_folder, "range.mp4");

        Assert.Equal(0, Program.Main(["export", entry, "-o", output, "--from", "30", "--to", "4s"]));

        AssertVideo(output, 1920, 1080, 90);
        var expected = Path.Combine(_folder, "range-30.png");
        Assert.Equal(0, Program.Main(["render-frame", entry, "-f", "30", "-o", expected]));
        Assert.True(Psnr(LoadPng(expected), DecodeFrame(output, 0, 30)) >= 38);
    }

    [Fact]
    public void VoicesSoundWhereTheyArePlaced()
    {
        Samples.RequireFfmpeg();
        var entry = Samples.CopyToTemp("yukkuri", _folder);
        var output = Path.Combine(_folder, "voices.wav");
        Assert.Equal(0, Program.Main(["export", entry, "-o", output]));

        var scene = new ProjectStore().Load(entry).Root;
        var wav = WavReader.Read(output);
        var samplesPerFrame = wav.SampleRate / scene.Fps.ToDouble();
        var voices = scene.Items.Values.OfType<VoiceItem>().OrderBy(item => item.Start).ToList();
        Assert.Equal(4, voices.Count);
        foreach (var voice in voices)
        {
            // 声のファイルと書き出した音を突き合わせ、いちばん似ている位置を探す（置いた位置の前後 5 フレーム）
            var key = Assert.IsType<SynthesizedVoice>(voice.Source).CacheKey;
            var source = WavReader.Read(Path.Combine(Path.GetDirectoryName(entry)!, "voices", key + ".wav")).Samples;
            var placed = (long)(voice.Start * samplesPerFrame);
            var found = BestMatch(wav.Samples, source, placed, (int)(5 * samplesPerFrame));
            Assert.InRange((found - placed) / samplesPerFrame, -1, 1);
        }
    }

    // mix（ステレオ）の中で、voice（モノラル）の先頭 0.4 秒といちばん似ている位置（サンプル）。center の前後 range サンプルを探す
    private static long BestMatch(float[] mix, float[] voice, long center, int range)
    {
        var length = Math.Min(voice.Length, 19200);
        double Score(long position)
        {
            double sum = 0;
            for (var i = 0; i < length; i += 2)
            {
                var index = (position + i) * 2;
                if (index >= 0 && index < mix.Length)
                {
                    sum += mix[index] * voice[i];
                }
            }

            return sum;
        }

        // 粗く探してから、見つかった近くを 1 サンプルずつ探す
        var best = Enumerable.Range(-range / 16, range / 8 + 1).Select(step => center + step * 16L).MaxBy(Score);
        return Enumerable.Range(-16, 33).Select(offset => best + offset).MaxBy(Score);
    }

    // 映像と音の形式、フレーム数、長さ、全フレームのデコード
    private static void AssertVideo(string path, int width, int height, long frames)
    {
        var info = MediaProbe.Probe(path);
        var video = Assert.IsType<VideoStreamInfo>(info.Video);
        var audio = Assert.IsType<AudioStreamInfo>(info.Audio);
        Assert.Equal(("h264", width, height, "yuv420p"), (video.Codec, video.Width, video.Height, video.PixelFormat));
        Assert.Equal((Rational)30, video.FrameRate);
        Assert.Equal(frames, video.FrameCount);
        Assert.InRange(info.Duration!.Value.ToDouble(), (frames - 1) / 30.0, (frames + 1) / 30.0);
        Assert.Equal(("aac", 48000, 2), (audio.Codec, audio.SampleRate, audio.Channels));

        // 先頭から順にすべてのフレームをデコードできる
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions(MaxWidth: 320, MaxHeight: 320, Hardware: HardwareDecoding.Disabled, CacheCapacity: 2));
        var times = new HashSet<Rational>();
        for (var frame = 0L; frame < frames; frame++)
        {
            times.Add(decoder.GetFrame(TimeMath.FrameToSeconds(frame, 30)).Time);
        }

        Assert.Equal(frames, times.Count);
    }

    // PATH に ffprobe・ffmpeg があれば、色の情報と、デコードのエラーがないことを確かめる（なければ確かめない）
    private static void AssertWithExternalTools(string path)
    {
        if (Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_space,color_transfer,color_primaries", "-of", "csv=p=0", path]) is { } colors)
        {
            Assert.Equal("bt709,bt709,bt709", colors.Output.Trim());
        }

        if (Run("ffmpeg", ["-v", "error", "-i", path, "-f", "null", "-"]) is { } decode)
        {
            Assert.Equal("", decode.Error.Trim());
        }
    }

    private static (string Output, string Error)? Run(string tool, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (output.Result, error);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static BgraImage DecodeFrame(string path, long frame, Rational fps)
    {
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions(Hardware: HardwareDecoding.Disabled));
        return decoder.GetFrame(TimeMath.FrameToSeconds(frame, fps)).Image;
    }

    private static BgraImage LoadPng(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        using var converted = bitmap.Copy(SKColorType.Bgra8888);
        return new BgraImage(converted.Width, converted.Height, converted.Bytes);
    }

    // RGB の PSNR（dB）
    private static double Psnr(BgraImage expected, BgraImage actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        double squares = 0;
        for (var i = 0; i < expected.Pixels.Length; i += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                double difference = expected.Pixels[i + channel] - actual.Pixels[i + channel];
                squares += difference * difference;
            }
        }

        var mse = squares / (expected.Width * expected.Height * 3);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
