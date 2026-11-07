using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Engine.Subtitles;
using NakkuntMovieMaker.Media;
using SkiaSharp;

namespace NakkuntMovieMaker.Cli.Tests;

/// <summary>
/// nmm export と nmm render-frame。160×90・30fps・2 秒のプロジェクト（前半は赤、後半は青。前半にセリフと音）を保存して、コマンドで書き出す
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _entry;

    public CliTests()
    {
        Directory.CreateDirectory(_folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-1s.wav"), Path.Combine(_folder, "tone.wav"));
        var project = Project.CreateNew("テスト", new SceneFormat(160, 90, 30, 48000), 3);
        var asset = new Asset(AssetId.New(), AssetKind.Audio, AssetLinks.ToStoredPath(Path.Combine(_folder, "tone.wav"), _folder));
        var scene = project.Root;
        var red = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 0, 30, new ShapeStyle(ShapeKind.Rectangle, 160, 90, new SolidPaint(new RgbaColor(255, 0, 0))));
        var blue = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 30, 30, new ShapeStyle(ShapeKind.Rectangle, 160, 90, new SolidPaint(new RgbaColor(0, 0, 255))));
        var tone = new AudioItem(ItemId.New(), scene.Layers[1].Id, 0, 30, asset.Id);
        var voice = new VoiceItem(ItemId.New(), scene.Layers[2].Id, 15, 30, CharacterId.New(), "こんにちは");
        scene = scene with { Items = scene.Items.Add(red.Id, red).Add(blue.Id, blue).Add(tone.Id, tone).Add(voice.Id, voice) };
        project = project.WithScene(scene) with { Assets = project.Assets.Add(asset.Id, asset) };
        _entry = Path.Combine(_folder, "テスト.nmmproj");
        new ProjectStore().Save(project, _entry);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static void RequireFfmpeg()
    {
        var loaded = FfmpegLibrary.TryLoad(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") != "1", $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    private string Output(string name) => Path.Combine(_folder, "out", name);

    [Fact]
    public void ExportsVideoWithSubtitles()
    {
        RequireFfmpeg();

        var exitCode = Program.Main(["export", _entry, "-o", Output("a.mp4"), "--software", "--from", "0.5s", "--to", "45", "--subtitles", Output("a.srt")]);

        Assert.Equal(0, exitCode);
        var info = MediaProbe.Probe(Output("a.mp4"));
        Assert.Equal(("h264", 160, 90, 30L), (info.Video!.Codec, info.Video.Width, info.Video.Height, info.Video.FrameCount));
        Assert.Equal("aac", info.Audio!.Codec);
        // 範囲の始まり（15 フレーム目）を 0 秒にした字幕
        Assert.Equal([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "こんにちは")], Srt.ReadFile(Output("a.srt")));
    }

    [Fact]
    public void ExportsWithPresetAndCodec()
    {
        RequireFfmpeg();

        var exitCode = Program.Main(["export", _entry, "-o", Output("b.mp4"), "--preset", "light-720p", "--width", "128", "--height", "72", "--codec", "hevc", "--software", "--to", "10"]);

        Assert.Equal(0, exitCode);
        var video = MediaProbe.Probe(Output("b.mp4")).Video!;
        Assert.Equal(("hevc", 128, 72, 10L), (video.Codec, video.Width, video.Height, video.FrameCount));
    }

    [Fact]
    public void ExportsWithAnExternalFfmpeg()
    {
        RequireFfmpeg();
        var ffmpeg = FindOnPath(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        Assert.SkipWhen(ffmpeg is null || !ExternalFfmpeg.Probe(ffmpeg).Supports(VideoCodecKind.H264), "PATH に x264 を含む ffmpeg がないためスキップします");

        var exitCode = Program.Main(["export", _entry, "-o", Output("x264.mp4"), "--ffmpeg", ffmpeg!, "--to", "10"]);

        Assert.Equal(0, exitCode);
        var info = MediaProbe.Probe(Output("x264.mp4"));
        Assert.Equal(("h264", 10L, "aac"), (info.Video!.Codec, info.Video.FrameCount, info.Audio!.Codec));
    }

    [Fact]
    public void ReportsAMissingExternalFfmpeg()
    {
        RequireFfmpeg();

        var exitCode = Program.Main(["export", _entry, "-o", Output("x264.mp4"), "--ffmpeg", Path.Combine(_folder, "ない", "ffmpeg.exe")]);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(Output("x264.mp4")));
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);

    [Fact]
    public void ExportsWav()
    {
        RequireFfmpeg();

        Assert.Equal(0, Program.Main(["export", _entry, "-o", Output("c.wav"), "--to", "00:00:01"]));

        var wav = WavReader.Read(Output("c.wav"));
        Assert.Equal((2, 48000L), (wav.Channels, wav.FrameCount));
    }

    [Fact]
    public void RendersFrames()
    {
        Assert.Equal(0, Program.Main(["render-frame", _entry, "--frame", "45", "-o", Output("d.png")]));
        Assert.Equal(0, Program.Main(["render-frame", _entry, "-f", "0.1s", "-o", Output("e.png"), "--width", "320", "--height", "180"]));

        using (var image = SKBitmap.Decode(Output("d.png")))
        {
            Assert.Equal(new SKColor(0, 0, 255), image.GetPixel(80, 45));
        }

        using (var image = SKBitmap.Decode(Output("e.png")))
        {
            Assert.Equal((320, new SKColor(255, 0, 0)), (image.Width, image.GetPixel(160, 90)));
        }
    }

    [Fact]
    public void ChoosesTheRenderer()
    {
        // CPU を指定すれば、指定しないときと同じ画像になる。GPU（Vulkan）を使えなければ、CPU に切り替えて描く
        Assert.Equal(0, Program.Main(["render-frame", _entry, "--frame", "45", "-o", Output("auto.png")]));
        Assert.Equal(0, Program.Main(["render-frame", _entry, "--frame", "45", "-o", Output("cpu.png"), "--renderer", "cpu"]));
        Assert.Equal(0, Program.Main(["render-frame", _entry, "--frame", "45", "-o", Output("gpu.png"), "--renderer", "vulkan"]));
        Assert.Equal(File.ReadAllBytes(Output("auto.png")), File.ReadAllBytes(Output("cpu.png")));
        Assert.True(File.Exists(Output("gpu.png")));
        Assert.Equal(2, Program.Main(["render-frame", _entry, "-o", Output("x.png"), "--renderer", "opengl"]));
    }

    [Fact]
    public void RejectsBadArguments()
    {
        Assert.Equal(2, Program.Main(["export", _entry, "-o", Output("x.mp4"), "--preset", "nothing"]));
        Assert.Equal(2, Program.Main(["export", _entry, "-o", Output("x.mp4"), "--codec", "vp9"]));
        Assert.Equal(2, Program.Main(["export", _entry, "-o", Output("x.mp4"), "--from", "abc"]));
        Assert.Equal(2, Program.Main(["export", _entry, "-o", Output("x.mp4"), "--scene", "ない"]));
        Assert.Equal(2, Program.Main(["render-frame", Path.Combine(_folder, "ない.nmmproj"), "-o", Output("x.png")]));
        Assert.NotEqual(0, Program.Main(["export", _entry]));
        Assert.False(Directory.Exists(Path.Combine(_folder, "out")) && Directory.EnumerateFiles(Path.Combine(_folder, "out")).Any());
    }

    [Theory]
    [InlineData("90", 90L)]
    [InlineData("1.5s", 45L)]
    [InlineData("00:01:02.5", 1875L)]
    [InlineData("01:00", 1800L)]
    [InlineData("", null)]
    public void ParsesPositions(string text, long? expected)
    {
        Assert.True(ExportCommands.TryParsePosition(text, 30, out var frame));
        Assert.Equal(expected, frame);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("1:2:3:4")]
    [InlineData("xs")]
    public void RejectsBadPositions(string text) => Assert.False(ExportCommands.TryParsePosition(text, 30, out _));
}
