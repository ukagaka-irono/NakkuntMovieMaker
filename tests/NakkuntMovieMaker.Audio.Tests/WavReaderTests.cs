namespace NakkuntMovieMaker.Audio.Tests;

public sealed class WavReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N") + ".wav");

    public WavReaderTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    public void Dispose() => File.Delete(_path);

    // 16 ビットは、書くときは × 32767、読むときは ÷ 32768 なので、量子化の誤差に加えて最大で 1/32768 ずれる
    [Theory]
    [InlineData(WavSampleFormat.Pcm16, 2 / 32768.0)]
    [InlineData(WavSampleFormat.Float32, 0)]
    public void ReadsWhatWavWriterWrote(WavSampleFormat format, double tolerance)
    {
        float[] samples = [0f, 0.5f, -0.25f, 0.999f, -1f, 0.125f];

        WavWriter.WriteFile(_path, samples, channels: 2, sampleRate: 44100, format);
        var wav = WavReader.Read(_path);

        Assert.Equal((2, 44100, 3L), (wav.Channels, wav.SampleRate, wav.FrameCount));
        Assert.All(samples.Zip(wav.Samples), pair => Assert.Equal(pair.First, pair.Second, tolerance + 1e-6));
    }

    [Fact]
    public void Reads24BitAndSkipsOtherChunks()
    {
        // fmt（24 ビット・モノラル）、LIST（読み飛ばす）、data（3 サンプル）
        byte[] fmt = [1, 0, 1, 0, 0x80, 0xBB, 0, 0, 0x80, 0x32, 2, 0, 3, 0, 24, 0];
        byte[] list = [(byte)'L', (byte)'I', (byte)'S', (byte)'T', 3, 0, 0, 0, 1, 2, 3, 0];
        byte[] data = [0, 0, 0x40, 0xFF, 0xFF, 0xFF, 0, 0, 0x80];
        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(0));
        bytes.AddRange("WAVEfmt "u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(fmt.Length));
        bytes.AddRange(fmt);
        bytes.AddRange(list);
        bytes.AddRange("data"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(data.Length));
        bytes.AddRange(data);

        var wav = WavReader.Read(bytes.ToArray());

        Assert.Equal((1, 48000), (wav.Channels, wav.SampleRate));
        Assert.Equal([0.5f, -1 / 8388608f, -1f], wav.Samples);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Throws<InvalidDataException>(() => WavReader.Read("not a wav"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => WavReader.Read("RIFF\0\0\0\0WAVE"u8.ToArray()));
    }
}
