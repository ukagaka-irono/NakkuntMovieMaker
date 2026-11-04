using System.Globalization;
using System.Runtime.Versioning;
using System.Security;

namespace NakkuntMovieMaker.Voice.Sapi;

/// <summary>
/// Windows に入っている音声合成（SAPI 5）。Windows なら導入なしで使える。
/// COM を使うので、合成は STA のスレッドで行う（VoiceService がエンジン用のスレッドを STA にする）。
/// Windows 以外では「使用不可」になる
/// </summary>
public sealed class SapiEngine : IVoiceEngine
{
    public const string EngineId = "sapi";

    /// <summary>OS の既定の声</summary>
    public const string DefaultVoice = "default";

    private const int SampleRate = 48000;

    // SpeechAudioFormatType の SAFT48kHz16BitMono
    private const int Format48kHz16BitMono = 38;

    // SpeechVoiceSpeakFlags の SVSFIsXML（ピッチの指定に SAPI の XML を使う）
    private const int SpeakXml = 8;

    private readonly Lazy<(VoiceEngineStatus Status, IReadOnlyList<VoiceInfo> Voices)> _state;

    public SapiEngine()
    {
        _state = new Lazy<(VoiceEngineStatus, IReadOnlyList<VoiceInfo>)>(Inspect);
    }

    public string Id => EngineId;

    public string Name => "Windows の音声合成（SAPI）";

    public string Version => "5";

    public VoiceEngineStatus Status => _state.Value.Status;

    public IReadOnlyList<VoiceInfo> Voices => _state.Value.Voices;

    public bool RequiresSingleThreadedApartment => true;

    /// <summary>SAPI は本文をそのまま読むので、読みは使わない</summary>
    public string? MakeReading(string text) => null;

    public VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new VoiceEngineException("SAPI は Windows でだけ使えます");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return SynthesizeOnWindows(request);
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// 速さ（1 が標準）を SAPI の Rate（−10〜10）にする。SAPI の Rate は ±10 でおよそ 3 倍・1/3 倍になる
    /// </summary>
    public static int ToRate(double speed) =>
        (int)Math.Clamp(Math.Round(10 * Math.Log(Math.Max(speed, 0.01)) / Math.Log(3)), -10, 10);

    /// <summary>高さ（1 が標準）を SAPI の XML の pitch（−10〜10）にする。1 オクターブ（2 倍）を 10 とする</summary>
    public static int ToPitch(double pitch) =>
        (int)Math.Clamp(Math.Round(10 * Math.Log2(Math.Max(pitch, 0.01))), -10, 10);

    [SupportedOSPlatform("windows")]
    private static VoiceAudio SynthesizeOnWindows(VoiceRequest request)
    {
        dynamic voice = CreateCom("SAPI.SpVoice") ?? throw new VoiceEngineException("SAPI を使えません");
        dynamic stream = CreateCom("SAPI.SpMemoryStream") ?? throw new VoiceEngineException("SAPI を使えません");
        try
        {
            if (request.VoiceId != DefaultVoice)
            {
                voice.Voice = FindToken(voice, request.VoiceId) ?? throw new VoiceEngineException($"SAPI の声「{request.VoiceId}」が見つかりません");
            }

            stream.Format.Type = Format48kHz16BitMono;
            voice.AudioOutputStream = stream;
            voice.Rate = ToRate(request.Params.Speed);
            voice.Volume = 100;
            var pitch = ToPitch(request.Params.Pitch).ToString(CultureInfo.InvariantCulture);
            voice.Speak($"<pitch absmiddle=\"{pitch}\">{SecurityElement.Escape(request.Text)}</pitch>", SpeakXml);

            byte[] data = (byte[])stream.GetData();
            var samples = new float[data.Length / 2];
            var gain = (float)Math.Max(0, request.Params.Volume);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Clamp(BitConverter.ToInt16(data, i * 2) / 32768f * gain, -1f, 1f);
            }

            return new VoiceAudio(samples, SampleRate);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new VoiceEngineException($"SAPI で合成できませんでした（{ex.Message}）", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static dynamic? FindToken(dynamic voice, string id)
    {
        dynamic tokens = voice.GetVoices();
        for (var i = 0; i < (int)tokens.Count; i++)
        {
            dynamic token = tokens.Item(i);
            if ((string)token.Id == id)
            {
                return token;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static object? CreateCom(string progId) =>
        Type.GetTypeFromProgID(progId) is { } type ? Activator.CreateInstance(type) : null;

    // 使えるかと声の一覧を調べる（COM のオブジェクトはその場で作って捨てるので、どのスレッドから呼んでもよい）
    private static (VoiceEngineStatus, IReadOnlyList<VoiceInfo>) Inspect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (new VoiceEngineStatus(false, "SAPI は Windows でだけ使えます"), []);
        }

        try
        {
            dynamic? voice = CreateCom("SAPI.SpVoice");
            if (voice is null)
            {
                return (new VoiceEngineStatus(false, "この Windows には SAPI が入っていません"), []);
            }

            var voices = new List<VoiceInfo> { new(DefaultVoice, "既定の声") };
            dynamic tokens = voice.GetVoices();
            for (var i = 0; i < (int)tokens.Count; i++)
            {
                dynamic token = tokens.Item(i);
                voices.Add(new VoiceInfo((string)token.Id, (string)token.GetDescription()));
            }

            return voices.Count > 1
                ? (VoiceEngineStatus.Ready, voices)
                : (new VoiceEngineStatus(false, "Windows に音声合成の声が入っていません（設定の「音声」から追加できます）"), []);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return (new VoiceEngineStatus(false, $"SAPI を使えません（{ex.Message}）"), []);
        }
    }
}
