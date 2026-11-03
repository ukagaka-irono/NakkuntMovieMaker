using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering.Effects;

namespace NakkuntMovieMaker.Rendering.Transitions;

/// <summary>
/// トランジションの種類の定義。パラメーターの定義はエフェクトと同じ形で持つ。
/// 描画の処理（CPU・GPU）は、描画の処理の側で Type ごとに用意する
/// </summary>
public abstract class TransitionDefinition
{
    /// <summary>種類の ID（"nmm.crossfade" など）</summary>
    public abstract string Type { get; }

    /// <summary>画面に出す名前</summary>
    public abstract string Name { get; }

    public virtual ImmutableArray<ParameterDefinition> Parameters => [];
}

/// <summary>トランジションの種類の一覧。組み込みのトランジションを登録してある</summary>
public sealed class TransitionRegistry
{
    private readonly ImmutableDictionary<string, TransitionDefinition> _definitions;

    public TransitionRegistry(IEnumerable<TransitionDefinition> definitions)
    {
        _definitions = definitions.ToImmutableDictionary(definition => definition.Type, StringComparer.Ordinal);
    }

    /// <summary>組み込みのトランジションだけを登録したもの</summary>
    public static TransitionRegistry Default { get; } = new(BuiltInTransitions.All);

    public IEnumerable<TransitionDefinition> All => _definitions.Values.OrderBy(definition => definition.Type, StringComparer.Ordinal);

    public TransitionDefinition? Find(string type) => _definitions.GetValueOrDefault(type);
}

/// <summary>
/// 組み込みのトランジション。向き（direction）は、後のアイテムが入ってくる側
/// （fromLeft なら左から現れる）
/// </summary>
public static class BuiltInTransitions
{
    private static readonly string[] Directions = ["fromLeft", "fromRight", "fromTop", "fromBottom"];

    public static readonly TransitionDefinition Crossfade = new Simple("nmm.crossfade", "クロスフェード");

    public static readonly TransitionDefinition Dip = new Simple(
        "nmm.dip",
        "色を挟んだフェード（黒・白へのフェード）",
        ParameterDefinition.Color("color", "色", RgbaColor.Black));

    public static readonly TransitionDefinition Wipe = new Simple(
        "nmm.wipe",
        "ワイプ",
        ParameterDefinition.Choice("direction", "向き", "fromLeft", Directions),
        ParameterDefinition.Number("softness", "境界のぼかし（%）", 10, 0, 100));

    public static readonly TransitionDefinition Slide = new Simple(
        "nmm.slide",
        "スライド",
        ParameterDefinition.Choice("direction", "向き", "fromRight", Directions));

    public static readonly TransitionDefinition Push = new Simple(
        "nmm.push",
        "押し出し",
        ParameterDefinition.Choice("direction", "向き", "fromRight", Directions));

    public static readonly TransitionDefinition Zoom = new Simple(
        "nmm.zoom",
        "拡大縮小",
        ParameterDefinition.Number("amount", "拡大率（%）", 50, 0, 500));

    public static readonly TransitionDefinition Spin = new Simple(
        "nmm.spin",
        "回転",
        ParameterDefinition.Number("angle", "回す角度（度）", 180, -1080, 1080));

    public static ImmutableArray<TransitionDefinition> All { get; } = [Crossfade, Dip, Wipe, Slide, Push, Zoom, Spin];

    private sealed class Simple(string type, string name, params ParameterDefinition[] parameters) : TransitionDefinition
    {
        public override string Type => type;

        public override string Name => name;

        public override ImmutableArray<ParameterDefinition> Parameters { get; } = [.. parameters];
    }
}
