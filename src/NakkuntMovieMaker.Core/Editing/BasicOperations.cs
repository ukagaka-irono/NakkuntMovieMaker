using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Editing;

// モデルを 1 か所だけ変える基本の操作。編集コマンド（EditCommands）はこれを Transaction にまとめて作る。
// 基本の操作は重なりやロックを確かめない（それは編集コマンドの役目）

/// <summary>アイテムを追加する</summary>
public sealed record AddItem(SceneId Scene, Item Item) : IOperation
{
    public string Description => "アイテムの追加";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        if (scene.Items.ContainsKey(Item.Id))
        {
            throw new EditException("同じ ID のアイテムがすでにあります");
        }

        if (scene.FindLayer(Item.Layer) is null)
        {
            throw new EditException("アイテムを置くレイヤーが見つかりません");
        }

        return new OperationResult(
            project.WithScene(scene with { Items = scene.Items.Add(Item.Id, Item) }),
            new RemoveItem(Scene, Item.Id),
            [DirtyRange.OfItem(Scene, Item)]);
    }
}

/// <summary>アイテムを削除する</summary>
public sealed record RemoveItem(SceneId Scene, ItemId Item) : IOperation
{
    public string Description => "アイテムの削除";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        var removed = scene.GetItem(Item);
        return new OperationResult(
            project.WithScene(scene with { Items = scene.Items.Remove(Item) }),
            new AddItem(Scene, removed),
            [DirtyRange.OfItem(Scene, removed)]);
    }
}

/// <summary>アイテムを、同じ ID の新しい内容に置き換える</summary>
public sealed record ReplaceItem(SceneId Scene, Item Item) : IOperation
{
    public string Description => "アイテムの変更";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        var old = scene.GetItem(Item.Id);
        if (scene.FindLayer(Item.Layer) is null)
        {
            throw new EditException("アイテムを置くレイヤーが見つかりません");
        }

        return new OperationResult(
            project.WithScene(scene with { Items = scene.Items.SetItem(Item.Id, Item) }),
            new ReplaceItem(Scene, old),
            [DirtyRange.OfItem(Scene, old), DirtyRange.OfItem(Scene, Item)]);
    }
}

/// <summary>レイヤーを指定した位置（0 が一番下）に挿入する</summary>
public sealed record InsertLayer(SceneId Scene, int Index, Layer Layer) : IOperation
{
    public string Description => "レイヤーの追加";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        if (Index < 0 || Index > scene.Layers.Count)
        {
            throw new EditException("レイヤーを挿入する位置が範囲外です");
        }

        if (scene.FindLayer(Layer.Id) is not null)
        {
            throw new EditException("同じ ID のレイヤーがすでにあります");
        }

        return new OperationResult(
            project.WithScene(scene with { Layers = scene.Layers.Insert(Index, Layer) }),
            new RemoveLayer(Scene, Layer.Id),
            [DirtyRange.WholeScene(Scene)]);
    }
}

/// <summary>空のレイヤーを削除する。アイテムが残っているレイヤーは削除できない</summary>
public sealed record RemoveLayer(SceneId Scene, LayerId Layer) : IOperation
{
    public string Description => "レイヤーの削除";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        var index = scene.LayerIndex(Layer);
        if (index < 0)
        {
            throw new EditException("レイヤーが見つかりません");
        }

        if (scene.Items.Values.Any(item => item.Layer == Layer))
        {
            throw new EditException("アイテムが残っているレイヤーは削除できません");
        }

        return new OperationResult(
            project.WithScene(scene with { Layers = scene.Layers.RemoveAt(index) }),
            new InsertLayer(Scene, index, scene.Layers[index]),
            [DirtyRange.WholeScene(Scene)]);
    }
}

/// <summary>レイヤーを、同じ ID の新しい内容（名前・表示・ロックなど）に置き換える</summary>
public sealed record ReplaceLayer(SceneId Scene, Layer Layer) : IOperation
{
    public string Description => "レイヤーの変更";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        var index = scene.LayerIndex(Layer.Id);
        if (index < 0)
        {
            throw new EditException("レイヤーが見つかりません");
        }

        return new OperationResult(
            project.WithScene(scene with { Layers = scene.Layers.SetItem(index, Layer) }),
            new ReplaceLayer(Scene, scene.Layers[index]),
            [DirtyRange.WholeScene(Scene)]);
    }
}

/// <summary>レイヤーの表示順を変える</summary>
public sealed record MoveLayer(SceneId Scene, LayerId Layer, int NewIndex) : IOperation
{
    public string Description => "レイヤーの並べ替え";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        var oldIndex = scene.LayerIndex(Layer);
        if (oldIndex < 0)
        {
            throw new EditException("レイヤーが見つかりません");
        }

        if (NewIndex < 0 || NewIndex >= scene.Layers.Count)
        {
            throw new EditException("レイヤーの移動先が範囲外です");
        }

        var layer = scene.Layers[oldIndex];
        return new OperationResult(
            project.WithScene(scene with { Layers = scene.Layers.RemoveAt(oldIndex).Insert(NewIndex, layer) }),
            new MoveLayer(Scene, Layer, oldIndex),
            [DirtyRange.WholeScene(Scene)]);
    }
}

/// <summary>マーカーを追加・変更・削除する。Marker が null なら削除</summary>
public sealed record SetMarker(SceneId Scene, MarkerId Id, Marker? Marker) : IOperation
{
    public string Description => Marker is null ? "マーカーの削除" : "マーカーの設定";

    public OperationResult Apply(Project project)
    {
        var scene = project.GetScene(Scene);
        var index = scene.Markers.FindIndex(marker => marker.Id == Id);
        var old = index >= 0 ? scene.Markers[index] : null;

        var markers = (old, Marker) switch
        {
            (null, null) => scene.Markers,
            (null, _) => scene.Markers.Add(Marker),
            (_, null) => scene.Markers.RemoveAt(index),
            _ => scene.Markers.SetItem(index, Marker),
        };

        // マーカーは描画に影響しないので、描き直しは要らない
        return new OperationResult(project.WithScene(scene with { Markers = markers }), new SetMarker(Scene, Id, old), []);
    }
}

/// <summary>キャラクターを追加・変更・削除する。Character が null なら削除</summary>
public sealed record SetCharacter(CharacterId Id, Character? Character) : IOperation
{
    public string Description => Character is null ? "キャラクターの削除" : "キャラクターの設定";

    public OperationResult Apply(Project project)
    {
        var old = project.Characters.GetValueOrDefault(Id);
        var characters = Character is null ? project.Characters.Remove(Id) : project.Characters.SetItem(Id, Character);
        return new OperationResult(project with { Characters = characters }, new SetCharacter(Id, old), [DirtyRange.Everything]);
    }
}

/// <summary>素材を追加・変更・削除する。Asset が null なら削除</summary>
public sealed record SetAsset(AssetId Id, Asset? Asset) : IOperation
{
    public string Description => Asset is null ? "素材の削除" : "素材の設定";

    public OperationResult Apply(Project project)
    {
        var old = project.Assets.GetValueOrDefault(Id);
        var assets = Asset is null ? project.Assets.Remove(Id) : project.Assets.SetItem(Id, Asset);
        return new OperationResult(project with { Assets = assets }, new SetAsset(Id, old), [DirtyRange.Everything]);
    }
}

/// <summary>シーンを、同じ ID の新しい内容に丸ごと置き換える（名前や形式の変更など）</summary>
public sealed record ReplaceScene(Scene Scene) : IOperation
{
    public string Description => "シーンの設定";

    public OperationResult Apply(Project project)
    {
        var old = project.GetScene(Scene.Id);
        return new OperationResult(project.WithScene(Scene), new ReplaceScene(old), [DirtyRange.WholeScene(Scene.Id)]);
    }
}

/// <summary>プロジェクトの名前を変える</summary>
public sealed record RenameProject(string Name) : IOperation
{
    public string Description => "プロジェクトの名前の変更";

    public OperationResult Apply(Project project) =>
        new(project with { Name = Name }, new RenameProject(project.Name), []);
}

public static class Operations
{
    /// <summary>複数の操作を 1 つの Transaction にまとめる</summary>
    public static Transaction Combine(string description, IEnumerable<IOperation> operations) =>
        new(description, [.. operations]);
}
