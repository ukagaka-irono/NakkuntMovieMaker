using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NakkuntMovieMaker.Voice.ColorfulTalk;

/// <summary>
/// ColorfulTalk（旧版 v1）。規則合成（フォルマント合成）の音声合成エンジンで、ライブラリ（colorful_talk）を同梱する
/// （tools/fetch-native でソースを取得し、tools/build-native でビルドする）。
/// 読みはカナとアクセント記号の「記号列」で、漢字は読めない。カナだけの本文はそのまま読み、漢字を含む本文には読みを入れてもらう。
/// ライブラリの関数はスレッドセーフ
/// </summary>
public sealed partial class ColorfulTalkEngine : IVoiceEngine
{
    public const string EngineId = "colorfultalk1";

    /// <summary>合成する音のサンプリング周波数（ColorfulTalk の上限）</summary>
    public const int SampleRate = 48000;

    private const string Library = "colorful_talk";

    // ライブラリのエラーコード（colorful_talk.h）
    private const int ErrorUtf8 = 2;
    private const int ErrorChar = 3;
    private const int ErrorAccent = 4;
    private const int ErrorLong = 5;
    private const int ErrorDevoice = 6;
    private const int ErrorEmpty = 7;
    private const int ErrorParam = 8;
    private const int ErrorTooLong = 9;
    private const int ErrorMemory = 10;

    private readonly Lazy<(VoiceEngineStatus Status, string Version)> _state = new(Inspect);

    public string Id => EngineId;

    public string Name => "ColorfulTalk（旧版）";

    /// <summary>ライブラリの版（読み込めなければ "unavailable"）。版が変われば声のキャッシュを作り直す</summary>
    public string Version => _state.Value.Version;

    public VoiceEngineStatus Status => _state.Value.Status;

    public IReadOnlyList<VoiceInfo> Voices { get; } =
    [
        new("f1", "女性"),
        new("m1", "男性"),
        new("child", "子ども"),
        new("robot", "ロボット"),
    ];

    /// <summary>本文から記号列を作る（カナだけの本文。漢字などがあれば VoiceEngineException）</summary>
    public string? MakeReading(string text) => KoeText.FromText(text);

    public unsafe VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
    {
        if (!Status.Available)
        {
            throw new VoiceEngineException(Status.Message!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var koe = request.Reading ?? KoeText.FromText(request.Text);
        var parameters = new CTalkParams
        {
            Voice = Math.Max(0, Voices.ToList().FindIndex(voice => voice.Id == request.VoiceId)),
            Speed = Scale(request.Params.Speed, 50, 300),
            Pitch = Scale(request.Params.Pitch, 50, 200),
            Accent = Scale(request.Params.Intonation, 0, 200),
            Volume = Scale(request.Params.Volume, 0, 200),
            SampleRate = SampleRate,
        };

        short* pcm = null;
        var result = CTalk_SynthePCM(koe, &parameters, &pcm, out var count, out var rate);
        if (result != 0)
        {
            throw new VoiceEngineException(DescribeError(result, CTalk_ErrorPos(), koe));
        }

        try
        {
            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = pcm[i] / 32768f;
            }

            return new VoiceAudio(samples, rate);
        }
        finally
        {
            CTalk_FreeWave(pcm);
        }
    }

    public void Dispose()
    {
    }

    // 1 を標準とする倍率を、ColorfulTalk の 100 を標準とする値にする
    private static int Scale(double value, int min, int max) => Math.Clamp((int)Math.Round(value * 100), min, max);

    // 読みのどこが悪いかを、文字の位置（1 始まり）とともに知らせる
    private static string DescribeError(int code, int position, string koe)
    {
        var reason = code switch
        {
            ErrorUtf8 => "文字コードが正しくありません",
            ErrorChar => "記号列に使えない文字があります（漢字・英数字は読めないので、カナで書いてください）",
            ErrorAccent => "アクセント記号 ' の位置が正しくありません（句の始めには置けず、1 つの句に 1 つまで）",
            ErrorLong => "長音 ー の位置が正しくありません（句の始めや っ の後には置けません）",
            ErrorDevoice => "無声化の記号 _ の位置が正しくありません（後ろにカナが必要です）",
            ErrorEmpty => "発音する文字がありません",
            ErrorParam => "声の調整の値が範囲外です",
            ErrorTooLong => "読みが長すぎます。セリフを分けてください",
            ErrorMemory => "メモリが足りません",
            _ => $"合成できませんでした（エラー {code}）",
        };
        if (position < 0 || code is ErrorEmpty or ErrorParam or ErrorTooLong or ErrorMemory)
        {
            return $"ColorfulTalk: {reason}";
        }

        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(koe);
        var at = position < elements.Length ? System.Globalization.StringInfo.GetNextTextElement(koe, elements[position]) : "";
        return $"ColorfulTalk: 読みの {position + 1} 文字目{(at.Length > 0 ? $"（「{at}」）" : "")}：{reason}";
    }

    private static (VoiceEngineStatus, string) Inspect()
    {
        try
        {
            var version = Marshal.PtrToStringUTF8(CTalk_Version()) ?? "?";
            return (VoiceEngineStatus.Ready, version);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (new VoiceEngineStatus(
                false,
                "ColorfulTalk のライブラリ（colorful_talk）が見つかりません。開発環境では tools/fetch-native と tools/build-native を実行してください",
                "https://github.com/ukagaka-irono/colorful-talk"), "unavailable");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CTalkParams
    {
        public int Voice;
        public int Speed;
        public int Pitch;
        public int Accent;
        public int Volume;
        public int SampleRate;
    }

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe partial int CTalk_SynthePCM(string koe, CTalkParams* parameters, short** pcm, out int samples, out int sampleRate);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe partial void CTalk_FreeWave(void* buffer);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int CTalk_ErrorPos();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial nint CTalk_Version();
}
