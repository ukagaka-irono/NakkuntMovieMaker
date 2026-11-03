using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Animation;

namespace NakkuntMovieMaker.Core.Model;

/// <summary>
/// アイテムに付けたエフェクト。Type はエフェクトの種類の ID（"nmm.blur" など）。
/// パラメーターの意味はエフェクトの種類ごとの定義に従う
/// </summary>
public sealed record EffectInstance(
    EffectId Id,
    string Type,
    ImmutableDictionary<string, ParameterValue> Parameters,
    bool Enabled = true);

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
