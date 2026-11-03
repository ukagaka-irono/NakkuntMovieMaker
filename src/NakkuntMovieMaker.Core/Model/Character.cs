namespace NakkuntMovieMaker.Core.Model;

/// <summary>キャラクター。声・字幕のスタイル・立ち絵をまとめて設定する</summary>
public sealed record Character(
    CharacterId Id,
    string Name,
    VoiceSpec Voice,
    TextStyle Subtitle,
    TachieSpec? Tachie = null);

/// <summary>
/// 使う声。EngineId は音声合成エンジン（"aquestalk1"、"sapi" など）、
/// VoiceId はそのエンジンの中の声の名前
/// </summary>
public sealed record VoiceSpec(string EngineId, string VoiceId, VoiceParams Params);

/// <summary>声の調整。どれも 1 が標準</summary>
public sealed record VoiceParams(double Speed = 1, double Pitch = 1, double Intonation = 1, double Volume = 1)
{
    public static readonly VoiceParams Default = new();
}

/// <summary>立ち絵の種類</summary>
public enum TachieKind
{
    /// <summary>画像 1 枚</summary>
    Simple,

    /// <summary>パーツ分けした立ち絵（体・目・口などのフォルダ）</summary>
    Parts,
}

/// <summary>立ち絵。Path は Simple なら画像ファイル、Parts なら素材のフォルダ</summary>
public sealed record TachieSpec(TachieKind Kind, string Path);
