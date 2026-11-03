using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Rendering;

/// <summary>
/// フレーム 1 枚に描く内容。GPU にも CPU にも依存しないデータで、描画の処理（IRenderBackend）がこれを実行する。
/// Width・Height はシーンの大きさ（ピクセル）で、Nodes は下から順に重ねる。
/// 中身で比較できるので、同じ内容のフレーム（止まっている画像だけの区間など）は描画した結果を使い回せる
/// </summary>
public sealed record FramePlan(int Width, int Height, RgbaColor Background, EquatableArray<RenderNode> Nodes);

/// <summary>描画の木の節</summary>
public abstract record RenderNode;

/// <summary>
/// 素材を 1 つ描く。描画の処理は、素材（Content）から切り抜き（Crop）で残した部分を、その中心がローカル座標の原点に
/// 来るように置き、Transform（ローカル座標 → シーンのピクセル座標）で変形して、不透明度と合成モードで重ねる。
/// ローカル座標の単位は素材のピクセル（動画・画像は元の大きさ。縮小してデコードしても単位は変えない）。
/// Item は元になったアイテム（プレビュー上で選択するときに使う）
/// </summary>
public sealed record DrawNode(
    ItemId? Item,
    DrawContent Content,
    Crop Crop,
    Matrix3x2 Transform,
    double Opacity,
    BlendMode Blend) : RenderNode;

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
