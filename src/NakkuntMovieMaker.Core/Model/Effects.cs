using System.Collections.Immutable;
using System.Text.Json;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Collections;

namespace NakkuntMovieMaker.Core.Model;

/// <summary>
/// アイテムに付けたエフェクト。Type はエフェクトの種類の ID（"nmm.blur" など）。
/// パラメーターの意味はエフェクトの種類ごとの定義に従う
/// </summary>
public sealed record EffectInstance(
    EffectId Id,
    string Type,
    ImmutableDictionary<string, ParameterValue> Parameters,
    bool Enabled = true)
{
    // Parameters は中身で比較する（ImmutableDictionary は参照で比較されるため）
    public bool Equals(EffectInstance? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Id == other.Id
            && Type == other.Type
            && Enabled == other.Enabled
            && CollectionEquality.DictionaryEquals(Parameters, other.Parameters));

    public override int GetHashCode() => HashCode.Combine(Id, Type, Enabled, Parameters.Count);
}

/// <summary>
/// アイテムの始まりにかけるトランジション。同じレイヤーで直前に隣り合うアイテム（終わりがこのアイテムの始まりと同じもの）から、
/// このアイテムへ切り替える。直前に隣り合うアイテムがなければ、何もないところから切り替える。
/// Type はトランジションの種類の ID（"nmm.crossfade" など）、Duration はかける長さ（フレーム数）で、切れ目を中心にかける。
/// パラメーターのキーフレームは、トランジションの始まりからの相対フレーム
/// </summary>
public sealed record TransitionInstance(string Type, long Duration, ImmutableDictionary<string, ParameterValue> Parameters)
{
    public TransitionInstance(string type, long duration)
        : this(type, duration, ImmutableDictionary<string, ParameterValue>.Empty)
    {
    }

    // Parameters は中身で比較する（ImmutableDictionary は参照で比較されるため）
    public bool Equals(TransitionInstance? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Type == other.Type
            && Duration == other.Duration
            && CollectionEquality.DictionaryEquals(Parameters, other.Parameters));

    public override int GetHashCode() => HashCode.Combine(Type, Duration, Parameters.Count);
}

/// <summary>エフェクトのパラメーターの値</summary>
public abstract record ParameterValue;

/// <summary>数値（キーフレームでアニメーションできる）</summary>
public sealed record NumberParameter(Animatable<double> Value) : ParameterValue;

/// <summary>色（キーフレームでアニメーションできる）</summary>
public sealed record ColorParameter(Animatable<RgbaColor> Value) : ParameterValue;

/// <summary>オン・オフ</summary>
public sealed record BoolParameter(bool Value) : ParameterValue;

/// <summary>文字列（ファイルのパスや選択肢の名前など）</summary>
public sealed record TextParameter(string Value) : ParameterValue;

/// <summary>知らない種類のパラメーター。保存されていた JSON そのものを持ち、保存するときにそのまま書き戻す</summary>
public sealed record UnknownParameter(JsonElement Data) : ParameterValue
{
    public bool Equals(UnknownParameter? other) => other is not null && JsonElement.DeepEquals(Data, other.Data);

    public override int GetHashCode() => Data.ValueKind.GetHashCode();
}
