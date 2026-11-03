using System.Buffers.Binary;

namespace NakkuntMovieMaker.Audio;

/// <summary>読み込んだ WAV。Samples はインターリーブの float32（-1〜1）</summary>
public sealed record WavData(float[] Samples, int Channels, int SampleRate)
{
    public long FrameCount => Samples.Length / Channels;
}

/// <summary>
/// WAV を読む（16・24・32 ビット整数と 32 ビット浮動小数点）。音声合成のキャッシュや録音のように、
/// FFmpeg を通さずに手早く読みたいときに使う
/// </summary>
public static class WavReader
{
    /// <exception cref="InvalidDataException">WAV ではない、または対応していない形式</exception>
    public static WavData Read(string path) => Read(File.ReadAllBytes(path), path);

    public static WavData Read(ReadOnlySpan<byte> bytes, string name = "WAV")
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"WAV の形式ではありません: {name}");
        }

        int? format = null;
        var channels = 0;
        var sampleRate = 0;
        var bits = 0;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = bytes.Slice(offset, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]), (uint)(bytes.Length - offset - 8));
            var body = bytes.Slice(offset + 8, size);
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);

                // WAVE_FORMAT_EXTENSIBLE は、サブフォーマットの先頭 2 バイトが本当の形式
                if (format == 0xFFFE && size >= 26)
                {
                    format = BinaryPrimitives.ReadInt16LittleEndian(body[24..]);
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is null || channels <= 0 || sampleRate <= 0)
                {
                    throw new InvalidDataException($"WAV の fmt が data より前にありません: {name}");
                }

                return new WavData(Decode(body, format.Value, bits, name), channels, sampleRate);
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"WAV に音のデータがありません: {name}");
    }

    private static float[] Decode(ReadOnlySpan<byte> data, int format, int bits, string name)
    {
        switch (format, bits)
        {
            case (1, 16):
            {
                var samples = new float[data.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
                }

                return samples;
            }

            case (1, 24):
            {
                var samples = new float[data.Length / 3];
                for (var i = 0; i < samples.Length; i++)
                {
                    var value = data[i * 3] | (data[i * 3 + 1] << 8) | ((sbyte)data[i * 3 + 2] << 16);
                    samples[i] = value / 8388608f;
                }

                return samples;
            }

            case (1, 32):
            {
                var samples = new float[data.Length / 4];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648f;
                }

                return samples;
            }

            case (3, 32):
            {
                var samples = new float[data.Length / 4];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
                }

                return samples;
            }

            default:
                throw new InvalidDataException($"対応していない WAV の形式です（形式 {format}、{bits} ビット）: {name}");
        }
    }
}
