using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// 使い捨ての画像（描画の途中で使う画像）を、大きさ・形式・使い方ごとに使い回す。GPU のスレッドだけで使う。
/// 返した画像は、その仕事が GPU で終わってから使い回す。使い回さずに持っておく量が maxBytes を超えたら捨てる
/// </summary>
public sealed class TexturePool(GpuAllocator allocator, long maxBytes) : IDisposable
{
    private readonly Dictionary<(int Width, int Height, VkFormat Format, VkImageUsageFlags Usage), Stack<GpuImage>> _free = [];
    private long _freeBytes;

    /// <summary>使い回せる画像の量（バイト）</summary>
    public long FreeBytes => _freeBytes;

    public GpuImage Rent(int width, int height, VkFormat format, VkImageUsageFlags usage)
    {
        if (_free.TryGetValue((width, height, format, usage), out var stack) && stack.TryPop(out var image))
        {
            _freeBytes -= image.Bytes;
            return image;
        }

        return allocator.CreateImage(width, height, format, usage);
    }

    /// <summary>画像を返す（context の仕事が GPU で終わってから、使い回せるようにする）</summary>
    public void Return(GpuCommandContext context, GpuImage image) => context.OnCompleted(() => Add(image));

    public void Dispose()
    {
        foreach (var image in _free.Values.SelectMany(stack => stack))
        {
            image.Dispose();
        }

        _free.Clear();
        _freeBytes = 0;
    }

    private void Add(GpuImage image)
    {
        if (_freeBytes + image.Bytes > maxBytes)
        {
            image.Dispose();
            return;
        }

        var key = (image.Width, image.Height, image.Format, ImageUsage(image));
        if (!_free.TryGetValue(key, out var stack))
        {
            _free[key] = stack = new Stack<GpuImage>();
        }

        stack.Push(image);
        _freeBytes += image.Bytes;
    }

    private static VkImageUsageFlags ImageUsage(GpuImage image) => image.Usage;
}

/// <summary>
/// GPU の資源（素材の画像、エフェクトの結果、LUT など）を覚えておく。量が maxBytes を超えたら、長く使っていないものから捨てる。
/// GPU がまだ使っているかもしれないので、捨てるのは、最後に使った仕事が GPU で終わってから（Collect で）。GPU のスレッドだけで使う
/// </summary>
public sealed class GpuCache<TKey>(long maxBytes) : IDisposable
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = new();
    private readonly List<(IDisposable Value, ulong LastUse)> _retired = [];

    public long Bytes { get; private set; }

    public int Count => _entries.Count;

    /// <summary>捨てることにしたが、まだ GPU が使っているかもしれないので残しているものの数</summary>
    public int RetiredCount => _retired.Count;

    /// <summary>覚えているものを取り出す。use は、それを使う仕事のタイムラインセマフォの値</summary>
    public bool TryGet<T>(TKey key, ulong use, out T value)
        where T : class, IDisposable
    {
        if (_entries.TryGetValue(key, out var node) && node.Value.Value is T found)
        {
            node.Value.LastUse = Math.Max(node.Value.LastUse, use);
            _order.Remove(node);
            _order.AddFirst(node);
            value = found;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>覚える（同じキーがあれば入れ替える）。use は、それを最初に使う仕事の値</summary>
    public void Add(TKey key, IDisposable value, long bytes, ulong use)
    {
        if (_entries.Remove(key, out var old))
        {
            Retire(old);
        }

        var node = _order.AddFirst(new Entry(key, value, bytes) { LastUse = use });
        _entries[key] = node;
        Bytes += bytes;
        while (Bytes > maxBytes && _order.Last is { } last && last != node)
        {
            _entries.Remove(last.Value.Key);
            Retire(last);
        }
    }

    /// <summary>すべてを捨てる（テーマや設定が変わったときなど）</summary>
    public void Clear()
    {
        foreach (var node in _entries.Values)
        {
            _retired.Add((node.Value.Value, node.Value.LastUse));
        }

        _entries.Clear();
        _order.Clear();
        Bytes = 0;
    }

    /// <summary>GPU で終わった仕事（completed まで）だけが使っていた、捨てたものを破棄する</summary>
    public void Collect(ulong completed)
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].LastUse <= completed)
            {
                _retired[i].Value.Dispose();
                _retired.RemoveAt(i);
            }
        }
    }

    /// <summary>すべてを破棄する（GPU がすべての仕事を終えてから呼ぶこと）</summary>
    public void Dispose()
    {
        Clear();
        Collect(ulong.MaxValue);
    }

    private void Retire(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        Bytes -= node.Value.Bytes;
        _retired.Add((node.Value.Value, node.Value.LastUse));
    }

    private sealed record Entry(TKey Key, IDisposable Value, long Bytes)
    {
        public ulong LastUse { get; set; }
    }
}
