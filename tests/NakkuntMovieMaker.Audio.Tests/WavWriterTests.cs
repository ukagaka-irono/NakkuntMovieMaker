using System.Buffers.Binary;

namespace NakkuntMovieMaker.Audio.Tests;

public sealed class WavWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N") + ".wav");

    public WavWriterTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void WritesPcm16WithAHeader()
    {
        WavWriter.WriteFile(_path, [0f, 0.5f, -1f, 2f], channels: 2, sampleRate: 48000);

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(44 + 8, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20))); // PCM
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        short[] samples = [.. Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2)))];
        Assert.Equal([0, 16384, -32767, 32767], samples.Select(value => (int)value)); // 範囲を超えた値は切り詰める
    }

    [Fact]
    public void WritesFloat32InPieces()
    {
        using (var writer = new WavWriter(_path, channels: 1, sampleRate: 44100, WavSampleFormat.Float32))
        {
            writer.Write([0.25f, -0.75f]);
            writer.Write([1.5f]);
            Assert.Equal(3, writer.FrameCount);
        }

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20))); // IEEE float
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(44 + 8)));
    }
}
