using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Services;

/// <summary>アイテムを画面に出すときの名前（タイムラインとプロパティパネルで使う）</summary>
public static class ItemLabels
{
    public static string Label(Item item, Project project) => item switch
    {
        VideoItem video => AssetName(project, video.Asset),
        ImageItem image => AssetName(project, image.Asset),
        AudioItem audio => AssetName(project, audio.Asset),
        TextItem text => text.Text.ReplaceLineEndings(" "),
        ShapeItem shape => ShapeName(shape.Shape.Kind),
        VoiceItem voice => $"{CharacterName(project, voice.Character)}「{voice.Text.ReplaceLineEndings(" ")}」",
        TachieItem tachie => $"立ち絵（{CharacterName(project, tachie.Character)}）",
        UnknownItem unknown => unknown.TypeId,
        _ => "",
    };

    public static string ShapeName(ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "四角形",
        ShapeKind.Ellipse => "円",
        ShapeKind.Triangle => "三角形",
        ShapeKind.Polygon => "多角形",
        ShapeKind.Star => "星形",
        ShapeKind.Line => "線",
        ShapeKind.Arrow => "矢印",
        ShapeKind.Background => "背景",
        _ => "図形",
    };

    public static string CharacterName(Project project, CharacterId id) =>
        project.Characters.TryGetValue(id, out var character) ? character.Name : "キャラクターなし";

    private static string AssetName(Project project, AssetId id) =>
        project.Assets.TryGetValue(id, out var asset) ? Path.GetFileName(asset.Path) : "（素材なし）";
}
