using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Tests.Editing;

/// <summary>
/// 編集のテスト用プロジェクト（30fps）。
/// レイヤー 0: 動画 [0, 90)・画像 [100, 160)
/// レイヤー 1: テキスト A [0, 30)・テキスト B [60, 90)（A と B は同じグループ）
/// レイヤー 2: 図形 [40, 50)
/// レイヤー 3: 空・レイヤー 4: ロック中で、セリフ [0, 60)
/// </summary>
internal sealed class TestProject
{
    public TestProject()
    {
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30, 5);
        var scene = project.Root;
        SceneId = scene.Id;
        Layers = [.. scene.Layers.Select(layer => layer.Id)];

        var asset = new Asset(AssetId.New(), AssetKind.Video, "video.mp4");
        var group = GroupId.New();
        var character = new Character(CharacterId.New(), "霊夢", new VoiceSpec("sapi", "default", VoiceParams.Default), TextStyle.Default);

        Video = new VideoItem(ItemId.New(), Layers[0], 0, 90, asset.Id)
        {
            Opacity = Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(60, 1)),
            Playback = MediaPlayback.Normal with { SourceOffset = 2 },
        };
        Image = new ImageItem(ItemId.New(), Layers[0], 100, 60, AssetId.New());
        TextA = new TextItem(ItemId.New(), Layers[1], 0, 30, "A") { Group = group };
        TextB = new TextItem(ItemId.New(), Layers[1], 60, 30, "B") { Group = group };
        Shape = new ShapeItem(ItemId.New(), Layers[2], 40, 10, new ShapeStyle(ShapeKind.Rectangle, 100, 100, new SolidPaint(RgbaColor.White)));
        Voice = new VoiceItem(ItemId.New(), Layers[4], 0, 60, character.Id, "ゆっくりしていってね");

        var items = new Item[] { Video, Image, TextA, TextB, Shape, Voice }.ToImmutableDictionary(item => item.Id);
        var layers = scene.Layers.SetItem(4, scene.Layers[4] with { Locked = true });
        Project = project.WithScene(scene with { Items = items, Layers = layers }) with
        {
            Assets = project.Assets.Add(asset.Id, asset),
            Characters = project.Characters.Add(character.Id, character),
        };
    }

    public Project Project { get; }

    public SceneId SceneId { get; }

    public IReadOnlyList<LayerId> Layers { get; }

    public VideoItem Video { get; }

    public ImageItem Image { get; }

    public TextItem TextA { get; }

    public TextItem TextB { get; }

    public ShapeItem Shape { get; }

    public VoiceItem Voice { get; }

    public Scene Scene(Project project) => project.GetScene(SceneId);

    public Project Apply(IOperation operation) => operation.Apply(Project).Project;
}
