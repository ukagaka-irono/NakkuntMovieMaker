using NakkuntMovieMaker.Voice;
using NakkuntMovieMaker.Voice.ColorfulTalk;
using NakkuntMovieMaker.Voice.Sapi;

namespace NakkuntMovieMaker.Engine;

/// <summary>組み込みの音声合成エンジンの一覧を作る</summary>
public static class VoiceEngines
{
    /// <summary>
    /// ColorfulTalk（旧版。同梱し、Windows・Linux・macOS で使える）と、SAPI（Windows だけ）
    /// </summary>
    public static VoiceEngineRegistry CreateDefault() => new([new ColorfulTalkEngine(), new SapiEngine()]);
}
