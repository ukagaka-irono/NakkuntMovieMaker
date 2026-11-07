using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Rendering.Color;
using NakkuntMovieMaker.Rendering.Effects;

namespace NakkuntMovieMaker.Rendering;

/// <summary>
/// フレーム 1 枚に描く内容。GPU にも CPU にも依存しないデータで、描画の処理（IRenderBackend）がこれを実行する。
/// Width・Height はシーンの大きさ（ピクセル）で、Nodes は下から順に重ねる。
/// 中身で比較できるので、同じ内容のフレーム（止まっている画像だけの区間など）は描画した結果を使い回せる。
/// Color は色の設定（合成の色空間など）で、既定値は M1 と同じ処理
/// </summary>
public sealed record FramePlan(int Width, int Height, RgbaColor Background, EquatableArray<RenderNode> Nodes)
{
    public FrameColor Color { get; init; } = FrameColor.Default;
}

/// <summary>
/// フレームを描くときの色の設定。Compositing がリニアなら、合成（重ねる・混ぜる・トランジション）をリニアの Rec.709 で計算する。
/// 素材・文字・図形の色と、出力は sRGB のまま。エフェクトの中の計算（色の処理・ぼかし）は、どちらでも sRGB で行う。
/// Output は、描き終えた画像（sRGB）にかける色の変換（プレビューの表示の変換・書き出しの変換）。null なら変換しない。
/// 比べるときは、同じ変換のインスタンス（ColorTransforms が覚えているもの）なら同じとみなす
/// </summary>
public sealed record FrameColor(CompositingSpace Compositing = CompositingSpace.Srgb, ColorLut? Output = null)
{
    public static FrameColor Default { get; } = new();

    public static FrameColor Linear { get; } = new(CompositingSpace.Linear);

    public bool IsLinear => Compositing == CompositingSpace.Linear;

    /// <summary>プロジェクトの色の設定から</summary>
    public static FrameColor From(ProjectColorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Compositing == CompositingSpace.Linear ? Linear : Default;
    }

    /// <summary>出力の変換を付けたもの</summary>
    public FrameColor WithOutput(ColorLut? output) => output is null && Output is null ? this : this with { Output = output };
}

/// <summary>描画の木の節</summary>
public abstract record RenderNode;

/// <summary>
/// 素材を 1 つ描く。描画の処理は、素材（Content）から切り抜き（Crop）で残した部分を、その中心がローカル座標の原点に
/// 来るように置き、Transform（ローカル座標 → シーンのピクセル座標）で変形して、不透明度と合成モードで重ねる。
/// ローカル座標の単位は素材のピクセル（動画・画像は元の大きさ。縮小してデコードしても単位は変えない）。
/// Item は元になったアイテム（プレビュー上で選択するときに使う）。
/// Effects は、切り抜いた素材にローカル座標で順に適用するエフェクトで、変形・不透明度・合成モードはその後に適用する
/// </summary>
public sealed record DrawNode(
    ItemId? Item,
    DrawContent Content,
    Crop Crop,
    Matrix3x2 Transform,
    double Opacity,
    BlendMode Blend,
    EquatableArray<EvaluatedEffect> Effects = default) : RenderNode;

/// <summary>
/// トランジション。From（前のアイテム）から To（後のアイテム）へ切り替える途中の絵を描く。
/// Progress は 0（すべて From）〜 1（すべて To）。From・To はないこともある（何もないところからの切り替えなど）
/// </summary>
public sealed record TransitionNode(string Type, double Progress, EffectParameters Parameters, RenderNode? From, RenderNode? To) : RenderNode;

/// <summary>描く素材の中身</summary>
public abstract record DrawContent;

/// <summary>動画の、素材の先頭から SourceTime 秒の時点に表示されているフレーム</summary>
public sealed record VideoFrameContent(AssetId Asset, Rational SourceTime) : DrawContent;

/// <summary>画像</summary>
public sealed record ImageContent(AssetId Asset) : DrawContent;

/// <summary>文字。VisibleCharacters は一文字ずつ表示するときに見えている文字の数（null なら全部）</summary>
public sealed record TextContent(string Text, TextStyle Style, int? VisibleCharacters = null) : DrawContent;

/// <summary>図形</summary>
public sealed record ShapeContent(ShapeStyle Shape) : DrawContent;

/// <summary>
/// 立ち絵。Layers は重ねる画像（ファイルの場所）を奥から順に並べたもの。左上をそろえて重ね、いちばん大きい画像の大きさを立ち絵の大きさにする
/// </summary>
public sealed record TachieContent(EquatableArray<string> Layers) : DrawContent;
