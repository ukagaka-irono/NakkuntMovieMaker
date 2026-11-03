using System.Collections.Immutable;
using System.Text.Json;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Model;

/// <summary>
/// タイムライン上のアイテム。Start と Length はシーンのフレーム単位。
/// キーフレームの Frame はアイテムの先頭からの相対フレーム
/// </summary>
public abstract record Item(ItemId Id, LayerId Layer, long Start, long Length)
{
    /// <summary>アイテムのグループ。同じグループのアイテムはまとめて選択・移動する</summary>
    public GroupId? Group { get; init; }

    /// <summary>エフェクト。上から順に適用する</summary>
    public ImmutableList<EffectInstance> Effects { get; init; } = [];

    /// <summary>アイテムの終わり（この直前のフレームまでが表示される）</summary>
    public long End => Start + Length;

    public bool Contains(long frame) => frame >= Start && frame < End;

    /// <summary>シーンのフレームを、アイテムの先頭からの相対フレームにする</summary>
    public long ToLocalFrame(long frame) => frame - Start;

    // Effects は中身で比較する（ImmutableList は参照で比較されるため）。派生 record の項目は、派生側の Equals が比較する
    public virtual bool Equals(Item? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && EqualityContract == other.EqualityContract
            && Id == other.Id
            && Layer == other.Layer
            && Start == other.Start
            && Length == other.Length
            && Group == other.Group
            && CollectionEquality.ListEquals(Effects, other.Effects));

    public override int GetHashCode() => HashCode.Combine(EqualityContract, Id, Layer, Start, Length, Group);
}

/// <summary>映像として描かれるアイテム</summary>
public abstract record VisualItem(ItemId Id, LayerId Layer, long Start, long Length) : Item(Id, Layer, Start, Length)
{
    public Transform2D Transform { get; init; } = Transform2D.Identity;

    /// <summary>不透明度（0〜1）</summary>
    public Animatable<double> Opacity { get; init; } = 1.0;

    public BlendMode Blend { get; init; } = BlendMode.Normal;

    /// <summary>切り抜き（素材の端から削るピクセル数）</summary>
    public Crop Crop { get; init; } = Crop.None;
}

/// <summary>動画</summary>
public sealed record VideoItem(ItemId Id, LayerId Layer, long Start, long Length, AssetId Asset)
    : VisualItem(Id, Layer, Start, Length)
{
    public MediaPlayback Playback { get; init; } = MediaPlayback.Normal;

    public AudioMix Audio { get; init; } = AudioMix.Default;
}

/// <summary>画像</summary>
public sealed record ImageItem(ItemId Id, LayerId Layer, long Start, long Length, AssetId Asset)
    : VisualItem(Id, Layer, Start, Length);

/// <summary>音声</summary>
public sealed record AudioItem(ItemId Id, LayerId Layer, long Start, long Length, AssetId Asset)
    : Item(Id, Layer, Start, Length)
{
    public MediaPlayback Playback { get; init; } = MediaPlayback.Normal;

    public AudioMix Audio { get; init; } = AudioMix.Default;
}

/// <summary>テキスト</summary>
public sealed record TextItem(ItemId Id, LayerId Layer, long Start, long Length, string Text)
    : VisualItem(Id, Layer, Start, Length)
{
    public TextStyle Style { get; init; } = TextStyle.Default;
}

/// <summary>図形</summary>
public sealed record ShapeItem(ItemId Id, LayerId Layer, long Start, long Length, ShapeStyle Shape)
    : VisualItem(Id, Layer, Start, Length);

/// <summary>
/// セリフ。声（音声合成か録音）と字幕をまとめて扱う。
/// 字幕のスタイルと声の設定は、指定がなければキャラクターのものを使う
/// </summary>
public sealed record VoiceItem(ItemId Id, LayerId Layer, long Start, long Length, CharacterId Character, string Text)
    : VisualItem(Id, Layer, Start, Length)
{
    /// <summary>読み（音声記号列）を手で直したもの。null なら本文から自動で作る</summary>
    public string? Reading { get; init; }

    public VoiceSource Source { get; init; } = new SynthesizedVoice();

    public VoiceParams? VoiceOverride { get; init; }

    public TextStyle? SubtitleOverride { get; init; }

    public AudioMix Audio { get; init; } = AudioMix.Default;
}

/// <summary>立ち絵。口パクは、時間が重なっている同じキャラクターのセリフの声に合わせる</summary>
public sealed record TachieItem(ItemId Id, LayerId Layer, long Start, long Length, CharacterId Character)
    : VisualItem(Id, Layer, Start, Length);

/// <summary>
/// 知らない種類のアイテム（プラグインが入っていない環境で開いたときなど）。
/// 描画はしないが、Data（保存されていた JSON のうち、共通の項目を除いた残り）を持ち、保存するときにそのまま書き戻す。
/// 共通の項目（種類・ID・レイヤー・開始・長さ・グループ・エフェクト）は、この record の値から書く
/// </summary>
public sealed record UnknownItem(ItemId Id, LayerId Layer, long Start, long Length, string TypeId, JsonElement Data)
    : Item(Id, Layer, Start, Length)
{
    public bool Equals(UnknownItem? other) =>
        base.Equals(other) && TypeId == other.TypeId && JsonElement.DeepEquals(Data, other.Data);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), TypeId);
}

/// <summary>セリフの声の出どころ</summary>
public abstract record VoiceSource;

/// <summary>音声合成で作る声。CacheKey は生成済みの音声（voices/&lt;CacheKey&gt;.wav）</summary>
public sealed record SynthesizedVoice(string? CacheKey = null) : VoiceSource;

/// <summary>録音した声</summary>
public sealed record RecordedVoice(AssetId Asset) : VoiceSource;

/// <summary>
/// 位置・拡大率・回転・中心点・反転。X・Y はシーンの中心を原点とするピクセル、
/// 拡大率は 1 が等倍、回転は度（時計回り）、中心点はアイテムの中心からのピクセル
/// </summary>
public sealed record Transform2D(
    Animatable<double> X,
    Animatable<double> Y,
    Animatable<double> ScaleX,
    Animatable<double> ScaleY,
    Animatable<double> Rotation,
    Animatable<double> AnchorX,
    Animatable<double> AnchorY,
    bool FlipHorizontal = false,
    bool FlipVertical = false)
{
    public static readonly Transform2D Identity = new(0.0, 0.0, 1.0, 1.0, 0.0, 0.0, 0.0);
}

/// <summary>切り抜き（素材の端から削るピクセル数）</summary>
public sealed record Crop(double Left, double Top, double Right, double Bottom)
{
    public static readonly Crop None = new(0, 0, 0, 0);
}

/// <summary>合成モード</summary>
public enum BlendMode
{
    Normal,
    Add,
    Subtract,
    Multiply,
    Screen,
    Overlay,
    Lighten,
    Darken,
    Difference,
}

/// <summary>
/// 動画・音声の再生のしかた。アイテムは素材の [SourceOffset, SourceOffset + 長さ × Speed) 秒の範囲を使い、
/// Reverse なら その範囲を後ろから再生する。Speed は再生速度（1 が等速）で、トリムで位置がずれないよう有理数で持つ
/// </summary>
public sealed record MediaPlayback(Rational SourceOffset, Rational Speed, bool Reverse = false)
{
    public static readonly MediaPlayback Normal = new(Rational.Zero, Rational.One);
}

/// <summary>
/// 音量・パン・フェード。Volume は倍率（1 が元の大きさ）、Pan は -1（左）〜 1（右）。
/// フェードはフレーム数。PreservePitch は速度を変えても音の高さを保つか
/// </summary>
public sealed record AudioMix(
    Animatable<double> Volume,
    Animatable<double> Pan,
    long FadeInFrames = 0,
    long FadeOutFrames = 0,
    bool PreservePitch = true)
{
    public static readonly AudioMix Default = new(1.0, 0.0);
}
