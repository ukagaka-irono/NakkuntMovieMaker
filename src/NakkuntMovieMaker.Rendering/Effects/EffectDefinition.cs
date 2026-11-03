using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Rendering.Effects;

/// <summary>パラメーターの種類</summary>
public enum ParameterKind
{
    /// <summary>数値（キーフレームでアニメーションできる）</summary>
    Number,

    /// <summary>色（キーフレームでアニメーションできる）</summary>
    Color,

    /// <summary>オン・オフ</summary>
    Bool,

    /// <summary>選択肢の中から 1 つ（値は選択肢の名前）</summary>
    Choice,

    /// <summary>ファイル（LUT など）。値はプロジェクトのフォルダからの相対パスか、絶対パス</summary>
    File,
}

/// <summary>
/// エフェクトのパラメーターの定義。Name は保存するときのキー、Label は画面に出す名前。
/// 数値は Min〜Max に収める。Default の型は、数値は double、色は RgbaColor、オン・オフは bool、選択肢とファイルは string
/// </summary>
public sealed record ParameterDefinition(
    string Name,
    string Label,
    ParameterKind Kind,
    object Default,
    double Min = double.NegativeInfinity,
    double Max = double.PositiveInfinity,
    ImmutableArray<string> Choices = default)
{
    public static ParameterDefinition Number(string name, string label, double value, double min = double.NegativeInfinity, double max = double.PositiveInfinity) =>
        new(name, label, ParameterKind.Number, value, min, max);

    public static ParameterDefinition Color(string name, string label, RgbaColor value) => new(name, label, ParameterKind.Color, value);

    public static ParameterDefinition Bool(string name, string label, bool value) => new(name, label, ParameterKind.Bool, value);

    public static ParameterDefinition Choice(string name, string label, string value, params string[] choices) =>
        new(name, label, ParameterKind.Choice, value, Choices: [.. choices]);

    public static ParameterDefinition File(string name, string label) => new(name, label, ParameterKind.File, "");
}

/// <summary>エフェクトを評価する時点（アイテムの先頭からのフレーム、アイテムの長さ、シーンのフレームレート）</summary>
public readonly record struct EffectTime(long LocalFrame, long Length, Rational Fps)
{
    /// <summary>アイテムの先頭からの秒</summary>
    public double Seconds => TimeMath.FrameToSeconds(LocalFrame, Fps).ToDouble();
}

/// <summary>
/// エフェクトの種類の定義。パラメーターの定義を持ち、画面（プロパティパネル）・保存・描画のすべてがこれに従う。
/// 描画の処理（CPU・GPU）は、描画の処理の側で Type ごとに用意する
/// </summary>
public abstract class EffectDefinition
{
    /// <summary>種類の ID（"nmm.blur" など）</summary>
    public abstract string Type { get; }

    /// <summary>画面に出す名前</summary>
    public abstract string Name { get; }

    public abstract ImmutableArray<ParameterDefinition> Parameters { get; }

    /// <summary>
    /// 時間で決まる値（フェードの係数、ノイズのシード、振動のずれなど）を、評価したパラメーターに加える。
    /// 描く内容が変わらないフレームでは同じ値になるようにする（描いた結果を使い回せるように）
    /// </summary>
    public virtual EffectParameters Resolve(EffectParameters parameters, EffectTime time) => parameters;

    public ParameterDefinition? FindParameter(string name) => Parameters.FirstOrDefault(parameter => parameter.Name == name);

    /// <summary>すべてのパラメーターを既定値にしたもの</summary>
    public EffectParameters Defaults() =>
        new(Parameters.ToImmutableSortedDictionary(parameter => parameter.Name, parameter => parameter.Default, StringComparer.Ordinal));
}

/// <summary>評価したエフェクト（ある時点でのパラメーターの値が決まったもの）</summary>
public sealed record EvaluatedEffect(string Type, EffectParameters Parameters);

/// <summary>
/// パラメーターの値の集まり。値は double・RgbaColor・bool・string のどれか。中身で比較できる
/// </summary>
public sealed class EffectParameters : IEquatable<EffectParameters>
{
    private readonly ImmutableSortedDictionary<string, object> _values;

    public EffectParameters(ImmutableSortedDictionary<string, object> values)
    {
        _values = values;
    }

    public IEnumerable<KeyValuePair<string, object>> Values => _values;

    public EffectParameters With(string name, object value) => new(_values.SetItem(name, value));

    public double GetNumber(string name) => _values.TryGetValue(name, out var value) && value is double number ? number : 0;

    public float GetFloat(string name) => (float)GetNumber(name);

    public RgbaColor GetColor(string name) => _values.TryGetValue(name, out var value) && value is RgbaColor color ? color : RgbaColor.Transparent;

    public bool GetBool(string name) => _values.TryGetValue(name, out var value) && value is true;

    public string GetText(string name) => _values.TryGetValue(name, out var value) && value is string text ? text : "";

    public bool Equals(EffectParameters? other) =>
        other is not null && _values.Count == other._values.Count
        && _values.All(pair => other._values.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));

    public override bool Equals(object? obj) => Equals(obj as EffectParameters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values)
        {
            hash.Add(key);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _values.Select(pair => $"{pair.Key}={pair.Value}"));
}

/// <summary>エフェクトの種類の一覧。組み込みのエフェクトを登録してある（M4 からプラグインのエフェクトも加える）</summary>
public sealed class EffectRegistry
{
    private readonly ImmutableDictionary<string, EffectDefinition> _definitions;

    public EffectRegistry(IEnumerable<EffectDefinition> definitions)
    {
        _definitions = definitions.ToImmutableDictionary(definition => definition.Type, StringComparer.Ordinal);
    }

    /// <summary>組み込みのエフェクトだけを登録したもの</summary>
    public static EffectRegistry Default { get; } = new(BuiltInEffects.All);

    public IEnumerable<EffectDefinition> All => _definitions.Values.OrderBy(definition => definition.Type, StringComparer.Ordinal);

    public EffectDefinition? Find(string type) => _definitions.GetValueOrDefault(type);
}
