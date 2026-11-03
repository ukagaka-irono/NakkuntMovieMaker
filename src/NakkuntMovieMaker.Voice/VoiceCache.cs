using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Voice;

/// <summary>
/// 合成した声のキャッシュ。プロジェクトの voices/&lt;キー&gt;.wav（16 ビット・モノラル・エンジンのサンプルレート）に置く。
/// キャッシュはプロジェクトと一緒に保存・共有するので、エンジンを入れていない環境（CI や、プロジェクトを受け取った人）でも、
/// 開いて書き出せる。キーは、エンジン・エンジンの版・声・調整・本文・読みから決める
/// </summary>
public sealed class VoiceCache
{
    public VoiceCache(string projectFolder)
    {
        Folder = Path.Combine(projectFolder, "voices");
    }

    public string Folder { get; }

    /// <summary>キャッシュのキー（SHA-256 の 16 進数）</summary>
    public static string Key(IVoiceEngine engine, VoiceRequest request, string reading)
    {
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var parameters = request.Params;
        var text = string.Join(
            "\n",
            engine.Id,
            engine.Version,
            request.VoiceId,
            Number(parameters.Speed),
            Number(parameters.Pitch),
            Number(parameters.Intonation),
            Number(parameters.Volume),
            request.Text,
            reading);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public string PathOf(string key) => Path.Combine(Folder, key + ".wav");

    public bool Contains(string key) => File.Exists(PathOf(key));

    /// <summary>声を保存する。一時ファイルに書いてから名前を変えるので、途中で止まっても壊れたファイルは残らない</summary>
    public void Save(string key, VoiceAudio audio)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(key);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            WavWriter.WriteFile(temp, audio.Samples, 1, audio.SampleRate, WavSampleFormat.Pcm16);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>保存した声を読む。なければ null</summary>
    /// <exception cref="InvalidDataException">壊れている</exception>
    public VoiceAudio? Load(string key)
    {
        var path = PathOf(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var wav = WavReader.Read(path);
        if (wav.Channels == 1)
        {
            return new VoiceAudio(wav.Samples, wav.SampleRate);
        }

        // 複数チャンネルなら、平均してモノラルにする
        var mono = new float[wav.FrameCount];
        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var channel = 0; channel < wav.Channels; channel++)
            {
                sum += wav.Samples[i * wav.Channels + channel];
            }

            mono[i] = sum / wav.Channels;
        }

        return new VoiceAudio(mono, wav.SampleRate);
    }
}
