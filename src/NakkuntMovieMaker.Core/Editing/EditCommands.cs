using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Editing;

/// <summary>コピーしたアイテム。Offset と LayerOffset は、選択の左上（最も早い開始・最も下のレイヤー）からの差</summary>
public sealed record ClipboardContent(ImmutableArray<ClipboardEntry> Entries);

public sealed record ClipboardEntry(Item Item, long Offset, int LayerOffset);

/// <summary>
/// 利用者の編集操作。今のプロジェクトの状態から、基本の操作をまとめた Transaction を作る。
/// 作るときに結果を確かめ、同じレイヤーでアイテムが重なる・ロック中のレイヤーを変えるなどの場合は EditException を投げる
/// </summary>
public static class EditCommands
{
    /// <summary>アイテムを追加する</summary>
    public static IOperation AddItem(Project project, SceneId scene, Item item)
    {
        var edit = new EditBuilder(project, scene);
        edit.RequireUnlocked(item.Layer);
        edit.Do(new AddItem(scene, item));
        return edit.Build("アイテムの追加");
    }

    /// <summary>アイテムの内容を変える（プロパティパネルでの変更など）</summary>
    public static IOperation UpdateItem(Project project, SceneId scene, Item item)
    {
        var edit = new EditBuilder(project, scene);
        edit.RequireUnlocked(edit.Scene.GetItem(item.Id).Layer);
        edit.RequireUnlocked(item.Layer);
        edit.Do(new ReplaceItem(scene, item));
        return edit.Build("アイテムの変更");
    }

    /// <summary>アイテムを移動する。同じグループのアイテムも一緒に動く</summary>
    public static IOperation MoveItems(Project project, SceneId scene, IEnumerable<ItemId> items, long frameDelta, int layerDelta = 0)
    {
        var edit = new EditBuilder(project, scene);
        var layers = edit.Scene.Layers;
        foreach (var item in edit.ExpandGroups(items))
        {
            var newStart = item.Start + frameDelta;
            if (newStart < 0)
            {
                throw new EditException("0 フレームより前には移動できません");
            }

            var newIndex = edit.Scene.LayerIndex(item.Layer) + layerDelta;
            if (newIndex < 0 || newIndex >= layers.Count)
            {
                throw new EditException("移動先のレイヤーがありません");
            }

            edit.RequireUnlocked(item.Layer);
            edit.RequireUnlocked(layers[newIndex].Id);
            edit.Do(new ReplaceItem(scene, item with { Start = newStart, Layer = layers[newIndex].Id }));
        }

        return edit.Build("移動");
    }

    /// <summary>アイテムの先頭を動かす（終わりは変えない）</summary>
    public static IOperation TrimStart(Project project, SceneId scene, ItemId item, long newStart)
    {
        var edit = new EditBuilder(project, scene);
        var target = edit.GetUnlockedItem(item);
        if (newStart < 0)
        {
            throw new EditException("0 フレームより前には伸ばせません");
        }

        edit.Do(new ReplaceItem(scene, ItemTiming.TrimStart(target, newStart, edit.Scene.Fps)));
        return edit.Build("長さの変更");
    }

    /// <summary>アイテムの終わりを動かす（先頭は変えない）</summary>
    public static IOperation TrimEnd(Project project, SceneId scene, ItemId item, long newEnd)
    {
        var edit = new EditBuilder(project, scene);
        var target = edit.GetUnlockedItem(item);
        edit.Do(new ReplaceItem(scene, ItemTiming.TrimEnd(target, newEnd, edit.Scene.Fps)));
        return edit.Build("長さの変更");
    }

    /// <summary>
    /// アイテムの終わりを動かし、同じレイヤーの後ろのアイテムを同じだけずらす（リップル）。
    /// 縮めれば後ろが詰まり、伸ばせば後ろが押し出される
    /// </summary>
    public static IOperation RippleTrimEnd(Project project, SceneId scene, ItemId item, long newEnd)
    {
        var edit = new EditBuilder(project, scene);
        var target = edit.GetUnlockedItem(item);
        var delta = newEnd - target.End;

        foreach (var follower in edit.Scene.ItemsOnLayer(target.Layer).Where(other => other.Start >= target.End).ToList())
        {
            edit.Do(new ReplaceItem(scene, follower with { Start = follower.Start + delta }));
        }

        edit.Do(new ReplaceItem(scene, ItemTiming.TrimEnd(target, newEnd, edit.Scene.Fps)));
        return edit.Build("長さの変更（リップル）");
    }

    /// <summary>隣り合う 2 つのアイテムの境目を動かす（ロール）。合計の長さは変わらない</summary>
    public static IOperation RollEdit(Project project, SceneId scene, ItemId left, ItemId right, long frame)
    {
        var edit = new EditBuilder(project, scene);
        var leftItem = edit.GetUnlockedItem(left);
        var rightItem = edit.GetUnlockedItem(right);
        if (leftItem.Layer != rightItem.Layer || leftItem.End != rightItem.Start)
        {
            throw new EditException("ロールできるのは、同じレイヤーで隣り合う 2 つのアイテムだけです");
        }

        var fps = edit.Scene.Fps;
        edit.Do(new ReplaceItem(scene, ItemTiming.TrimEnd(leftItem, frame, fps)));
        edit.Do(new ReplaceItem(scene, ItemTiming.TrimStart(rightItem, frame, fps)));
        return edit.Build("境目の移動（ロール）");
    }

    /// <summary>frame の位置で、そこにかかっているアイテムを分割する</summary>
    public static IOperation Split(Project project, SceneId scene, IEnumerable<ItemId> items, long frame)
    {
        var edit = new EditBuilder(project, scene);
        var targets = items.Select(edit.Scene.GetItem).Where(item => frame > item.Start && frame < item.End).ToList();
        if (targets.Count == 0)
        {
            throw new EditException("分割できるアイテムがありません");
        }

        foreach (var target in targets)
        {
            edit.RequireUnlocked(target.Layer);
            var (left, right) = ItemTiming.Split(target, frame, ItemId.New(), edit.Scene.Fps);
            edit.Do(new ReplaceItem(scene, left));
            edit.Do(new AddItem(scene, right));
        }

        return edit.Build("分割");
    }

    /// <summary>アイテムを削除する。同じグループのアイテムも一緒に消える</summary>
    public static IOperation DeleteItems(Project project, SceneId scene, IEnumerable<ItemId> items)
    {
        var edit = new EditBuilder(project, scene);
        foreach (var item in edit.ExpandGroups(items))
        {
            edit.RequireUnlocked(item.Layer);
            edit.Do(new RemoveItem(scene, item.Id));
        }

        return edit.Build("削除");
    }

    /// <summary>
    /// アイテムを削除して、空いた分だけ後ろを左に詰める。
    /// allLayers が false なら削除したアイテムと同じレイヤーだけ、true ならすべてのレイヤーを詰める
    /// </summary>
    public static IOperation RippleDelete(Project project, SceneId scene, IEnumerable<ItemId> items, bool allLayers = false)
    {
        var edit = new EditBuilder(project, scene);
        var deleted = edit.ExpandGroups(items).ToList();
        if (deleted.Count == 0)
        {
            return edit.Build("削除して左詰め");
        }

        foreach (var item in deleted)
        {
            edit.RequireUnlocked(item.Layer);
        }

        var deletedIds = deleted.Select(item => item.Id).ToHashSet();
        var remaining = edit.Scene.Items.Values.Where(item => !deletedIds.Contains(item.Id)).ToList();
        var shifts = new Dictionary<ItemId, long>();

        if (allLayers)
        {
            var gaps = MergeRanges(deleted);
            foreach (var item in remaining)
            {
                if (gaps.Any(gap => item.Start < gap.End && gap.Start < item.End))
                {
                    throw new EditException("ほかのレイヤーのアイテムが空いた範囲にかかっているため、全レイヤーを詰められません");
                }

                shifts[item.Id] = gaps.Where(gap => gap.End <= item.Start).Sum(gap => gap.End - gap.Start);
            }
        }
        else
        {
            foreach (var layerItems in deleted.GroupBy(item => item.Layer))
            {
                var gaps = MergeRanges(layerItems);
                foreach (var item in remaining.Where(item => item.Layer == layerItems.Key))
                {
                    shifts[item.Id] = gaps.Where(gap => gap.End <= item.Start).Sum(gap => gap.End - gap.Start);
                }
            }
        }

        foreach (var item in deleted)
        {
            edit.Do(new RemoveItem(scene, item.Id));
        }

        foreach (var (id, shift) in shifts.Where(pair => pair.Value != 0))
        {
            var item = edit.Scene.GetItem(id);
            edit.RequireUnlocked(item.Layer);
            edit.Do(new ReplaceItem(scene, item with { Start = item.Start - shift }));
        }

        return edit.Build("削除して左詰め");
    }

    /// <summary>アイテムを 1 つのグループにまとめる（すでにグループに入っていれば、そのグループごとまとめる）</summary>
    public static IOperation GroupItems(Project project, SceneId scene, IEnumerable<ItemId> items)
    {
        var edit = new EditBuilder(project, scene);
        var targets = edit.ExpandGroups(items).ToList();
        if (targets.Count < 2)
        {
            throw new EditException("グループにするには、アイテムを 2 つ以上選んでください");
        }

        var group = GroupId.New();
        foreach (var item in targets)
        {
            edit.RequireUnlocked(item.Layer);
            edit.Do(new ReplaceItem(scene, item with { Group = group }));
        }

        return edit.Build("グループ化");
    }

    /// <summary>アイテムのグループを解除する</summary>
    public static IOperation UngroupItems(Project project, SceneId scene, IEnumerable<ItemId> items)
    {
        var edit = new EditBuilder(project, scene);
        foreach (var item in edit.ExpandGroups(items).Where(item => item.Group is not null))
        {
            edit.RequireUnlocked(item.Layer);
            edit.Do(new ReplaceItem(scene, item with { Group = null }));
        }

        return edit.Build("グループ解除");
    }

    /// <summary>アイテムをコピーする。同じグループのアイテムも含める</summary>
    public static ClipboardContent Copy(Project project, SceneId scene, IEnumerable<ItemId> items)
    {
        var sceneData = project.GetScene(scene);
        var selected = new EditBuilder(project, scene).ExpandGroups(items).ToList();
        if (selected.Count == 0)
        {
            return new ClipboardContent([]);
        }

        var baseStart = selected.Min(item => item.Start);
        var baseLayer = selected.Min(item => sceneData.LayerIndex(item.Layer));
        return new ClipboardContent([
            .. selected
                .OrderBy(item => item.Start)
                .ThenBy(item => sceneData.LayerIndex(item.Layer))
                .Select(item => new ClipboardEntry(item, item.Start - baseStart, sceneData.LayerIndex(item.Layer) - baseLayer)),
        ]);
    }

    /// <summary>コピーしたアイテムを、frame の位置・layerIndex のレイヤーを起点に貼り付ける。ID とグループは新しくする</summary>
    public static IOperation Paste(Project project, SceneId scene, ClipboardContent content, long frame, int layerIndex)
    {
        var edit = new EditBuilder(project, scene);
        if (frame < 0)
        {
            throw new EditException("0 フレームより前には貼り付けられません");
        }

        var groups = new Dictionary<GroupId, GroupId>();
        foreach (var entry in content.Entries)
        {
            var index = layerIndex + entry.LayerOffset;
            if (index < 0 || index >= edit.Scene.Layers.Count)
            {
                throw new EditException("貼り付け先のレイヤーが足りません");
            }

            var layer = edit.Scene.Layers[index].Id;
            edit.RequireUnlocked(layer);

            GroupId? group = null;
            if (entry.Item.Group is { } oldGroup)
            {
                if (!groups.TryGetValue(oldGroup, out var newGroup))
                {
                    newGroup = GroupId.New();
                    groups[oldGroup] = newGroup;
                }

                group = newGroup;
            }

            edit.Do(new AddItem(scene, entry.Item with
            {
                Id = ItemId.New(),
                Start = frame + entry.Offset,
                Layer = layer,
                Group = group,
            }));
        }

        return edit.Build("貼り付け");
    }

    /// <summary>アイテムを複製し、選択範囲のすぐ後ろ（同じレイヤー）に置く</summary>
    public static IOperation Duplicate(Project project, SceneId scene, IEnumerable<ItemId> items)
    {
        var sceneData = project.GetScene(scene);
        var content = Copy(project, scene, items);
        if (content.Entries.IsEmpty)
        {
            throw new EditException("複製するアイテムを選んでください");
        }

        var selected = content.Entries.Select(entry => entry.Item).ToList();
        var end = selected.Max(item => item.End);
        var layerIndex = selected.Min(item => sceneData.LayerIndex(item.Layer));
        var paste = Paste(project, scene, content, end, layerIndex);
        return new Transaction("複製", [paste]);
    }

    /// <summary>レイヤーを追加する</summary>
    public static IOperation AddLayer(Project project, SceneId scene, int index, string name)
    {
        var edit = new EditBuilder(project, scene);
        edit.Do(new InsertLayer(scene, index, Layer.CreateNew(name)));
        return edit.Build("レイヤーの追加");
    }

    /// <summary>レイヤーを、上に置かれたアイテムごと削除する</summary>
    public static IOperation RemoveLayer(Project project, SceneId scene, LayerId layer)
    {
        var edit = new EditBuilder(project, scene);
        edit.RequireUnlocked(layer);
        foreach (var item in edit.Scene.ItemsOnLayer(layer).ToList())
        {
            edit.Do(new RemoveItem(scene, item.Id));
        }

        edit.Do(new RemoveLayer(scene, layer));
        return edit.Build("レイヤーの削除");
    }

    /// <summary>レイヤーの表示順を変える</summary>
    public static IOperation MoveLayer(Project project, SceneId scene, LayerId layer, int newIndex)
    {
        var edit = new EditBuilder(project, scene);
        edit.Do(new MoveLayer(scene, layer, newIndex));
        return edit.Build("レイヤーの並べ替え");
    }

    /// <summary>レイヤーの名前・表示・ロック・ミュート・ソロを変える</summary>
    public static IOperation UpdateLayer(Project project, SceneId scene, Layer layer)
    {
        var edit = new EditBuilder(project, scene);
        edit.Do(new ReplaceLayer(scene, layer));
        return edit.Build("レイヤーの変更");
    }

    // 重なっている・接している範囲をまとめる
    private static List<(long Start, long End)> MergeRanges(IEnumerable<Item> items)
    {
        var merged = new List<(long Start, long End)>();
        foreach (var item in items.OrderBy(item => item.Start))
        {
            if (merged.Count > 0 && item.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, item.End));
            }
            else
            {
                merged.Add((item.Start, item.End));
            }
        }

        return merged;
    }

    /// <summary>
    /// シーンの名前と形式（解像度・フレームレート・サンプルレート）を変える。フレームレートが変わるときは、
    /// アイテムの位置と長さ、マーカーを、同じ時刻になるよう新しいフレームレートに合わせる（長さは 1 フレーム以上）。
    /// キーフレームはアイテムの先頭からのフレーム数のまま変えない
    /// </summary>
    public static IOperation ChangeSceneSettings(Project project, SceneId sceneId, string name, SceneFormat format)
    {
        var scene = project.GetScene(sceneId);
        var from = scene.Fps;
        var to = format.Fps;
        long Convert(long frame) => from == to ? frame : Rational.Rescale(frame, 1 / from, 1 / to, Rounding.Nearest);

        var items = scene.Items.Values.Select(item =>
        {
            var start = Convert(item.Start);
            return item with { Start = start, Length = Math.Max(1, Convert(item.End) - start) };
        });
        var updated = scene with
        {
            Name = name,
            Format = format,
            Items = items.ToImmutableDictionary(item => item.Id),
            Markers = scene.Markers.Select(marker => marker with { Frame = Convert(marker.Frame) }).ToImmutableList(),
        };
        return new ReplaceScene(updated);
    }

    /// <summary>操作を順に適用しながら Transaction を組み立て、最後に結果を確かめる</summary>
    private sealed class EditBuilder(Project project, SceneId sceneId)
    {
        private readonly List<IOperation> _operations = [];
        private readonly Project _original = project;

        public Project Current { get; private set; } = project;

        public Scene Scene => Current.GetScene(sceneId);

        public void Do(IOperation operation)
        {
            Current = operation.Apply(Current).Project;
            _operations.Add(operation);
        }

        public void RequireUnlocked(LayerId layer)
        {
            if (_original.GetScene(sceneId).GetLayer(layer).Locked)
            {
                throw new EditException("ロックされたレイヤーは編集できません");
            }
        }

        public Item GetUnlockedItem(ItemId id)
        {
            var item = Scene.GetItem(id);
            RequireUnlocked(item.Layer);
            return item;
        }

        /// <summary>選んだアイテムに、同じグループのアイテムを加える（元の順番を保ち、重複は除く）</summary>
        public IEnumerable<Item> ExpandGroups(IEnumerable<ItemId> ids)
        {
            var scene = Scene;
            var selected = ids.Distinct().Select(scene.GetItem).ToList();
            var groups = selected.Select(item => item.Group).OfType<GroupId>().ToHashSet();
            var members = scene.Items.Values
                .Where(item => item.Group is { } group && groups.Contains(group))
                .OrderBy(item => item.Start)
                .ThenBy(item => item.Id.Value);
            return selected.Concat(members).DistinctBy(item => item.Id);
        }

        public Transaction Build(string description)
        {
            EnsureNoOverlap();
            return new Transaction(description, [.. _operations]);
        }

        // 変更したアイテムがあるレイヤーで、アイテムが重なっていないか確かめる。
        // 触っていないレイヤーは確かめない（読み込んだプロジェクトに元から重なりがあっても、ほかの編集を妨げないため）
        private void EnsureNoOverlap()
        {
            var before = _original.GetScene(sceneId).Items;
            var after = Scene.Items;
            var touchedLayers = new HashSet<LayerId>();
            foreach (var id in before.Keys.Union(after.Keys))
            {
                before.TryGetValue(id, out var oldItem);
                after.TryGetValue(id, out var newItem);
                if (!ReferenceEquals(oldItem, newItem))
                {
                    if (oldItem is not null)
                    {
                        touchedLayers.Add(oldItem.Layer);
                    }

                    if (newItem is not null)
                    {
                        touchedLayers.Add(newItem.Layer);
                    }
                }
            }

            foreach (var layer in after.Values.Where(item => touchedLayers.Contains(item.Layer)).GroupBy(item => item.Layer))
            {
                Item? previous = null;
                foreach (var item in layer.OrderBy(item => item.Start))
                {
                    if (previous is not null && item.Start < previous.End)
                    {
                        throw new EditException("同じレイヤーのアイテムが重なるため、この操作はできません");
                    }

                    previous = item;
                }
            }
        }
    }
}
