using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Tests.Editing;

public class OperationTests
{
    private static readonly TestProject T = new();

    // 基本の操作と編集コマンドを、名前付きで並べる（テストの表示名に使う）
    public static TheoryData<string> OperationNames => [.. Factories.Keys];

    private static readonly Dictionary<string, Func<TestProject, IOperation>> Factories = new()
    {
        ["基本:アイテムの追加"] = t => new AddItem(t.SceneId, new TextItem(ItemId.New(), t.Layers[3], 0, 10, "新規")),
        ["基本:アイテムの削除"] = t => new RemoveItem(t.SceneId, t.Shape.Id),
        ["基本:アイテムの置き換え"] = t => new ReplaceItem(t.SceneId, t.Shape with { Start = 70 }),
        ["基本:レイヤーの挿入"] = t => new InsertLayer(t.SceneId, 2, Layer.CreateNew("新規")),
        ["基本:空のレイヤーの削除"] = t => new RemoveLayer(t.SceneId, t.Layers[3]),
        ["基本:レイヤーの置き換え"] = t => new ReplaceLayer(t.SceneId, t.Scene(t.Project).Layers[0] with { Name = "背景" }),
        ["基本:レイヤーの移動"] = t => new MoveLayer(t.SceneId, t.Layers[0], 3),
        ["基本:マーカーの追加"] = t => NewMarker(t.SceneId),
        ["基本:キャラクターの削除"] = t => new SetCharacter(t.Voice.Character, null),
        ["基本:素材の追加"] = t => new SetAsset(AssetId.New(), new Asset(AssetId.New(), AssetKind.Image, "a.png")),
        ["移動"] = t => EditCommands.MoveItems(t.Project, t.SceneId, [t.Shape.Id], 5, 1),
        ["グループの移動"] = t => EditCommands.MoveItems(t.Project, t.SceneId, [t.TextA.Id], 3),
        ["先頭のトリム"] = t => EditCommands.TrimStart(t.Project, t.SceneId, t.Video.Id, 30),
        ["終わりのトリム"] = t => EditCommands.TrimEnd(t.Project, t.SceneId, t.Image.Id, 120),
        ["リップルトリム"] = t => EditCommands.RippleTrimEnd(t.Project, t.SceneId, t.Video.Id, 60),
        ["分割"] = t => EditCommands.Split(t.Project, t.SceneId, [t.Video.Id, t.Image.Id], 45),
        ["削除"] = t => EditCommands.DeleteItems(t.Project, t.SceneId, [t.TextA.Id]),
        ["削除して左詰め"] = t => EditCommands.RippleDelete(t.Project, t.SceneId, [t.Video.Id]),
        ["グループ化"] = t => EditCommands.GroupItems(t.Project, t.SceneId, [t.Video.Id, t.Shape.Id]),
        ["グループ解除"] = t => EditCommands.UngroupItems(t.Project, t.SceneId, [t.TextA.Id]),
        ["貼り付け"] = t => EditCommands.Paste(t.Project, t.SceneId, EditCommands.Copy(t.Project, t.SceneId, [t.TextA.Id]), 200, 1),
        ["複製"] = t => EditCommands.Duplicate(t.Project, t.SceneId, [t.Shape.Id]),
        ["レイヤーの追加"] = t => EditCommands.AddLayer(t.Project, t.SceneId, 0, "新規"),
        ["レイヤーの削除"] = t => EditCommands.RemoveLayer(t.Project, t.SceneId, t.Layers[1]),
    };

    private static SetMarker NewMarker(SceneId scene)
    {
        var id = MarkerId.New();
        return new SetMarker(scene, id, new Marker(id, 10, "ここ", RgbaColor.White));
    }

    [Theory]
    [MemberData(nameof(OperationNames))]
    public void InverseRestoresTheOriginalProject(string name)
    {
        var operation = Factories[name](T);

        var applied = operation.Apply(T.Project);
        var restored = applied.Inverse.Apply(applied.Project);

        Assert.NotEqual(T.Project, applied.Project);
        Assert.Equal(T.Project, restored.Project);
    }

    [Theory]
    [MemberData(nameof(OperationNames))]
    public void InverseOfInverseReappliesTheOperation(string name)
    {
        var operation = Factories[name](T);

        var applied = operation.Apply(T.Project);
        var restored = applied.Inverse.Apply(applied.Project);
        var reapplied = restored.Inverse.Apply(restored.Project);

        Assert.Equal(applied.Project, reapplied.Project);
    }

    [Fact]
    public void ReplaceItemMarksOldAndNewRangesDirty()
    {
        var result = new ReplaceItem(T.SceneId, T.Shape with { Start = 70 }).Apply(T.Project);

        Assert.Equal(
            [new DirtyRange(T.SceneId, 40, 50), new DirtyRange(T.SceneId, 70, 80)],
            result.Dirty);
    }

    [Fact]
    public void LayerAndCharacterChangesMarkWideRangesDirty()
    {
        var layer = new MoveLayer(T.SceneId, T.Layers[0], 1).Apply(T.Project);
        var character = new SetCharacter(T.Voice.Character, null).Apply(T.Project);

        Assert.Equal([DirtyRange.WholeScene(T.SceneId)], layer.Dirty);
        Assert.Equal([DirtyRange.Everything], character.Dirty);
        Assert.True(DirtyRange.Everything.Overlaps(T.SceneId, 0, 1));
    }

    [Fact]
    public void MarkersDoNotNeedRedraw()
    {
        var result = NewMarker(T.SceneId).Apply(T.Project);

        Assert.Empty(result.Dirty);
        Assert.Single(T.Scene(result.Project).Markers);
    }

    [Fact]
    public void LayersWithItemsCannotBeRemovedDirectly()
    {
        Assert.Throws<EditException>(() => new RemoveLayer(T.SceneId, T.Layers[0]).Apply(T.Project));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        Assert.Throws<EditException>(() => new AddItem(T.SceneId, T.Shape).Apply(T.Project));
    }
}
