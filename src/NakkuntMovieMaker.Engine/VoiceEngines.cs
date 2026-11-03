using NakkuntMovieMaker.Voice;
using NakkuntMovieMaker.Voice.AquesTalk;
using NakkuntMovieMaker.Voice.Sapi;

namespace NakkuntMovieMaker.Engine;

/// <summary>組み込みの音声合成エンジンの一覧を作る</summary>
public static class VoiceEngines
{
    /// <summary>
    /// SAPI と、AquesTalk1・AquesTalk10・AquesTalk2 のエンジン。AquesTalk は利用者が導入した場合だけ使え、
    /// AqKanji2Koe も導入されていれば、漢字かな交じり文の読みを作るのに使う。
    /// ConverterError は、AqKanji2Koe はあるが読み込めなかったときの理由
    /// </summary>
    public static (VoiceEngineRegistry Registry, string? ConverterError) CreateDefault(AquesTalkOptions? aquesTalk = null)
    {
        aquesTalk ??= AquesTalkOptions.Default;
        AqKanji2KoeConverter? converter = null;
        string? error = null;
        try
        {
            converter = AqKanji2KoeConverter.TryCreate(aquesTalk);
        }
        catch (VoiceEngineException ex)
        {
            error = ex.Message;
        }

        var registry = new VoiceEngineRegistry(
        [
            new SapiEngine(),
            new AquesTalk1Engine(aquesTalk, converter),
            new AquesTalk10Engine(aquesTalk, converter),
            new AquesTalk2Engine(aquesTalk, converter),
        ]);
        return (registry, error);
    }
}
