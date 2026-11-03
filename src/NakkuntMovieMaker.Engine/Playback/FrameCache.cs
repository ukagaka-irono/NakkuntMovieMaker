using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Playback;

/// <summary>
/// 描いたフレームを覚えておく（合計の大きさで上限を決め、使っていないものから捨てる）。
/// キーは FramePlan と出力の倍率なので、描く内容が同じフレーム（止まっている画像だけの区間など）は 1 枚を使い回し、
/// 編集で内容が変わったフレームは、FramePlan が変わるので自然に描き直しになる。
/// 素材の差し替えなど、FramePlan に表れない変更は Invalidate で捨てる。複数のスレッドから同時に使ってよい
/// </summary>
public sealed class FrameCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(FramePlan Plan, double Scale), LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _order = new();
    private long _bytes;

    /// <param name="capacityBytes">覚えておく画像の合計の上限（バイト）</param>
    public FrameCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }

    /// <summary>今覚えている画像の合計の大きさ（バイト）</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>覚えている画像を探す。見つかったら、そのフレームでも使ったことを記録する</summary>
    public bool TryGet(SceneId scene, long frame, FramePlan plan, double scale, out BgraImage image)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((plan, scale), out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                node.Value.Use(scene, frame);
                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    public void Add(SceneId scene, long frame, FramePlan plan, double scale, BgraImage image)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((plan, scale), out var existing))
            {
                existing.Value.Use(scene, frame);
                return;
            }

            var size = (long)image.Pixels.Length;
            if (size > CapacityBytes)
            {
                return;
            }

            var entry = new Entry((plan, scale), image, scene, frame);
            _entries[entry.Key] = _order.AddFirst(entry);
            _bytes += size;
            while (_bytes > CapacityBytes && _order.Last is { } oldest)
            {
                Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 範囲に重なるフレームで使った画像を捨てる。すべてのシーンにかかる範囲（素材・キャラクターの変更など）なら、すべて捨てる
    /// </summary>
    public void Invalidate(IEnumerable<DirtyRange> ranges)
    {
        lock (_gate)
        {
            foreach (var range in ranges)
            {
                if (range.Scene is null)
                {
                    Clear();
                    return;
                }

                for (var node = _order.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.Overlaps(range))
                    {
                        Remove(node);
                    }

                    node = next;
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _entries.Remove(node.Value.Key);
        _bytes -= node.Value.Image.Pixels.Length;
    }

    // 画像と、それを使ったフレームの範囲（シーンごとの最小〜最大。捨てるかどうかを多めに判断する）
    private sealed class Entry((FramePlan Plan, double Scale) key, BgraImage image, SceneId scene, long frame)
    {
        private readonly Dictionary<SceneId, (long First, long Last)> _frames = new() { [scene] = (frame, frame) };

        public (FramePlan Plan, double Scale) Key { get; } = key;

        public BgraImage Image { get; } = image;

        public void Use(SceneId scene, long frame) =>
            _frames[scene] = _frames.TryGetValue(scene, out var span) ? (Math.Min(span.First, frame), Math.Max(span.Last, frame)) : (frame, frame);

        public bool Overlaps(DirtyRange range) =>
            _frames.Any(pair => range.Overlaps(pair.Key, pair.Value.First, pair.Value.Last + 1));
    }
}
