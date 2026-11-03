using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine.Tachie;

namespace NakkuntMovieMaker.Engine;

/// <summary>
/// シーンのほかに、描く内容を決めるのに使うもの。Characters はプロジェクトのキャラクター（字幕のスタイルと立ち絵）、
/// Tachie は立ち絵の読み込み、LipSync は口パク用の声の大きさ（どれもなければ、立ち絵は描かず口も動かさない）
/// </summary>
public sealed record TimelineContext(
    IReadOnlyDictionary<CharacterId, Character> Characters,
    TachieLoader? Tachie = null,
    ILipSyncSource? LipSync = null)
{
    public static readonly TimelineContext Empty = new(new Dictionary<CharacterId, Character>());
}
