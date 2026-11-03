using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Model;

/// <summary>
/// シーン（タイムライン 1 本分）。Layers の並び順が表示順で、先頭が一番下に描かれる。
/// アイテムはレイヤーを ID で参照するので、レイヤーを挿入してもアイテムは書き換わらない
/// </summary>
public sealed record Scene(
    SceneId Id,
    string Name,
    SceneFormat Format,
    ImmutableList<Layer> Layers,
    ImmutableDictionary<ItemId, Item> Items,
    ImmutableList<Marker> Markers)
{
    public static Scene CreateNew(string name, SceneFormat format, int layerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layerCount);

        var layers = Enumerable.Range(1, layerCount)
            .Select(number => Layer.CreateNew($"レイヤー {number}"))
            .ToImmutableList();
        return new Scene(
            SceneId.New(),
            name,
            format,
            layers,
            ImmutableDictionary<ItemId, Item>.Empty,
            ImmutableList<Marker>.Empty);
    }

    public Rational Fps => Format.Fps;

    /// <summary>シーンの長さ（一番後ろのアイテムの終わり）。アイテムがなければ 0</summary>
    public long Duration => Items.Count == 0 ? 0 : Items.Values.Max(item => item.End);

    /// <summary>レイヤーの表示順（0 が一番下）。見つからなければ -1</summary>
    public int LayerIndex(LayerId layer) => Layers.FindIndex(candidate => candidate.Id == layer);

    public Layer? FindLayer(LayerId layer) => Layers.Find(candidate => candidate.Id == layer);

    /// <summary>レイヤー上のアイテムを開始フレームの順に返す</summary>
    public IEnumerable<Item> ItemsOnLayer(LayerId layer) =>
        Items.Values.Where(item => item.Layer == layer).OrderBy(item => item.Start).ThenBy(item => item.Id.Value);

    /// <summary>指定したフレームに表示されているアイテム（レイヤーの下から順）</summary>
    public IEnumerable<Item> ItemsAt(long frame) =>
        Items.Values
            .Where(item => item.Contains(frame))
            .OrderBy(item => LayerIndex(item.Layer))
            .ThenBy(item => item.Start);
}

/// <summary>レイヤー</summary>
public sealed record Layer(
    LayerId Id,
    string Name,
    bool Visible = true,
    bool Locked = false,
    bool Muted = false,
    bool Solo = false)
{
    public static Layer CreateNew(string name) => new(LayerId.New(), name);
}

/// <summary>タイムライン上の目印</summary>
public sealed record Marker(MarkerId Id, long Frame, string Name, RgbaColor Color);
