using System.Runtime.CompilerServices;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Engine;

/// <summary>
/// シーンのアイテムを、レイヤーごとに始まりの順に並べた索引。フレームごとの評価で、すべてのアイテムを調べ直さずに、
/// そのフレームに関わるアイテムだけを二分探索で取り出す（長い動画ほどアイテムが多く、全体を調べると書き出しが遅くなる）。
/// シーンは変更できないので、シーンごとに 1 回だけ作って覚えておく
/// </summary>
internal sealed class SceneIndex
{
    private static readonly ConditionalWeakTable<Scene, SceneIndex> Cache = [];

    private readonly Dictionary<LayerId, LayerItems> _layers = [];
    private readonly LayerItems _voices;

    private SceneIndex(Scene scene)
    {
        foreach (var group in scene.Items.Values.GroupBy(item => item.Layer))
        {
            _layers[group.Key] = new LayerItems(group);
        }

        _voices = new LayerItems(scene.Items.Values.OfType<VoiceItem>());
    }

    public static SceneIndex Of(Scene scene) => Cache.GetValue(scene, static scene => new SceneIndex(scene));

    /// <summary>レイヤーのアイテムのうち、frame に表示されるか、frame がトランジションの範囲に入りうるもの（始まりの順）</summary>
    public ReadOnlySpan<Item> Near(LayerId layer, long frame) =>
        _layers.TryGetValue(layer, out var items) ? items.Near(frame) : [];

    /// <summary>同じレイヤーで、item の始まりにちょうど終わるアイテム（始まりの順で最初のもの）</summary>
    public Item? EndingAt(LayerId layer, long end, ItemId except) =>
        _layers.TryGetValue(layer, out var items) ? items.EndingAt(end, except) : null;

    /// <summary>frame に関わりうるセリフ（どのレイヤーでも）</summary>
    public ReadOnlySpan<Item> VoicesNear(long frame) => _voices.Near(frame);

    private sealed class LayerItems
    {
        private readonly Item[] _sorted;
        private readonly long[] _starts;
        private readonly Dictionary<long, List<Item>> _byEnd = [];
        private readonly long _lag;
        private readonly long _lead;

        public LayerItems(IEnumerable<Item> items)
        {
            // Scene.ItemsOnLayer と同じ順（始まり、同じなら ID）
            _sorted = [.. items.OrderBy(item => item.Start).ThenBy(item => item.Id.Value)];
            _starts = [.. _sorted.Select(item => item.Start)];
            var longestTransition = 0L;
            var longest = 0L;
            foreach (var item in _sorted)
            {
                longest = Math.Max(longest, item.Length);
                longestTransition = Math.Max(longestTransition, item.Transition?.Duration ?? 0);
                if (!_byEnd.TryGetValue(item.End, out var ending))
                {
                    _byEnd[item.End] = ending = [];
                }

                ending.Add(item);
            }

            // 表示はアイテムの始まりから長さの分、トランジションは始まりの前後にトランジションの長さの分まで
            _lag = Math.Max(longest, longestTransition);
            _lead = longestTransition;
        }

        public ReadOnlySpan<Item> Near(long frame)
        {
            var from = LowerBound(frame - _lag + 1);
            var to = LowerBound(frame + _lead + 1);
            return _sorted.AsSpan(from, to - from);
        }

        public Item? EndingAt(long end, ItemId except)
        {
            if (_byEnd.TryGetValue(end, out var ending))
            {
                foreach (var item in ending)
                {
                    if (item.Id != except)
                    {
                        return item;
                    }
                }
            }

            return null;
        }

        // start 以上の始まりを持つ最初の位置
        private int LowerBound(long start)
        {
            var index = Array.BinarySearch(_starts, start);
            if (index < 0)
            {
                return ~index;
            }

            while (index > 0 && _starts[index - 1] == start)
            {
                index--;
            }

            return index;
        }
    }
}
