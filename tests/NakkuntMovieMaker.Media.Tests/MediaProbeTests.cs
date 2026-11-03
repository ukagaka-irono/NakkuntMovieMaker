using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media.Tests;

public class MediaProbeTests
{
    [Fact]
    public void ProbesVideoWithAudio()
    {
        Ffmpeg.RequireOrSkip();

        var info = MediaProbe.Probe(Ffmpeg.Fixture("red-blue-2s.mp4"));

        Assert.Equal(AssetKind.Video, info.Kind);
        Assert.Equal(2.0, info.Duration!.Value.ToDouble(), 2);
        var video = Assert.Single(info.VideoStreams);
        Assert.Equal(("h264", 320, 240), (video.Codec, video.Width, video.Height));
        Assert.Equal(new Rational(30, 1), video.FrameRate);
        Assert.Equal("yuv420p", video.PixelFormat);
        Assert.Equal(0, video.Rotation);
        var audio = Assert.Single(info.AudioStreams);
        Assert.Equal(("aac", 48000, 2), (audio.Codec, audio.SampleRate, audio.Channels));
    }

    [Fact]
    public void ProbesWebmWithoutAudio()
    {
        Ffmpeg.RequireOrSkip();

        var info = MediaProbe.Probe(Ffmpeg.Fixture("testsrc-1s.webm"));

        Assert.Equal(AssetKind.Video, info.Kind);
        Assert.Equal(("vp9", 320, 180), (info.Video!.Codec, info.Video.Width, info.Video.Height));
        Assert.Equal(new Rational(25, 1), info.Video.FrameRate);
        Assert.Empty(info.AudioStreams);
    }

    [Theory]
    [InlineData("green-64x48.png", "png")]
    [InlineData("yellow-64x48.jpg", "mjpeg")]
    public void ProbesImages(string file, string codec)
    {
        Ffmpeg.RequireOrSkip();

        var info = MediaProbe.Probe(Ffmpeg.Fixture(file));

        Assert.Equal(AssetKind.Image, info.Kind);
        Assert.Null(info.Duration);
        Assert.Equal((codec, 64, 48), (info.Video!.Codec, info.Video.Width, info.Video.Height));
        Assert.Null(info.Video.FrameRate);
    }

    [Theory]
    [InlineData("tone-1s.wav", "pcm_s16le", 48000, 1)]
    [InlineData("tone-1s.mp3", "mp3", 44100, 2)]
    public void ProbesAudio(string file, string codec, int sampleRate, int channels)
    {
        Ffmpeg.RequireOrSkip();

        var info = MediaProbe.Probe(Ffmpeg.Fixture(file));

        Assert.Equal(AssetKind.Audio, info.Kind);
        Assert.Empty(info.VideoStreams);
        Assert.Equal((codec, sampleRate, channels), (info.Audio!.Codec, info.Audio.SampleRate, info.Audio.Channels));
        Assert.Equal(1.0, info.Duration!.Value.ToDouble(), 1);
    }

    [Fact]
    public void RejectsMissingAndNonMediaFiles()
    {
        Ffmpeg.RequireOrSkip();

        Assert.Throws<MediaException>(() => MediaProbe.Probe(Ffmpeg.Fixture("does-not-exist.mp4")));
        Assert.Throws<MediaException>(() => MediaProbe.Probe(Ffmpeg.Fixture("not-media.txt")));
    }

    [Fact]
    public void HandlesNonAsciiPaths()
    {
        Ffmpeg.RequireOrSkip();
        var folder = Path.Combine(Path.GetTempPath(), "nmm-tests", "日本語のフォルダ " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "動画.mp4");
            File.Copy(Ffmpeg.Fixture("red-blue-2s.mp4"), path);

            Assert.Equal(AssetKind.Video, MediaProbe.Probe(path).Kind);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
