using System.Numerics;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.App.Services;

/// <summary>プレビューに描かれたアイテムの枠。Transform はローカル座標（素材の中心が原点）→ シーンのピクセル座標</summary>
public sealed record ItemOutline(ItemId Item, Matrix3x2 Transform, float Width, float Height)
{
    /// <summary>枠の四隅（シーンの座標。左上・右上・右下・左下の順）</summary>
    public Vector2[] Corners =>
    [
        Vector2.Transform(new Vector2(-Width / 2, -Height / 2), Transform),
        Vector2.Transform(new Vector2(Width / 2, -Height / 2), Transform),
        Vector2.Transform(new Vector2(Width / 2, Height / 2), Transform),
        Vector2.Transform(new Vector2(-Width / 2, Height / 2), Transform),
    ];

    /// <summary>枠の中心（シーンの座標）</summary>
    public Vector2 Center => Vector2.Transform(Vector2.Zero, Transform);

    /// <summary>シーンの点が枠の中にあるか</summary>
    public bool Contains(Vector2 point)
    {
        if (!Matrix3x2.Invert(Transform, out var inverse))
        {
            return false;
        }

        var local = Vector2.Transform(point, inverse);
        return Math.Abs(local.X) <= Width / 2 && Math.Abs(local.Y) <= Height / 2;
    }
}

/// <summary>プレビュー上でアイテムを選ぶ・動かす・拡大する・回転するための計算</summary>
public static class PreviewGeometry
{
    /// <summary>描いたフレームの、アイテムごとの枠（下から順。トランジション中のアイテムも含む）</summary>
    public static IReadOnlyList<ItemOutline> Outlines(FramePlan plan, ContentMeasurer measurer, IMediaSource media)
    {
        var outlines = new List<ItemOutline>();
        void Visit(RenderNode? node)
        {
            switch (node)
            {
                case DrawNode { Item: { } item } draw when measurer.Measure(draw, media, plan.Width, plan.Height) is var (width, height):
                    outlines.Add(new ItemOutline(item, draw.Transform, width, height));
                    break;
                case TransitionNode transition:
                    Visit(transition.From);
                    Visit(transition.To);
                    break;
            }
        }

        foreach (var node in plan.Nodes)
        {
            Visit(node);
        }

        return outlines;
    }

    /// <summary>点にあるいちばん上のアイテム（なければ null）。選んでいるアイテムがその点にあれば、それを優先する</summary>
    public static ItemId? HitTest(IReadOnlyList<ItemOutline> outlines, Vector2 point, IReadOnlySet<ItemId>? selected = null)
    {
        ItemId? top = null;
        foreach (var outline in outlines)
        {
            if (outline.Contains(point))
            {
                if (selected?.Contains(outline.Item) == true)
                {
                    return outline.Item;
                }

                top = outline.Item;
            }
        }

        return top;
    }

    /// <summary>位置をずらす（キーフレームがあれば、すべてのキーフレームを同じだけずらす）</summary>
    public static VisualItem Move(VisualItem item, double dx, double dy) =>
        item with { Transform = item.Transform with { X = Map(item.Transform.X, x => x + dx), Y = Map(item.Transform.Y, y => y + dy) } };

    /// <summary>拡大率を factor 倍にする（縦横とも）</summary>
    public static VisualItem Scale(VisualItem item, double factor) =>
        item with { Transform = item.Transform with { ScaleX = Map(item.Transform.ScaleX, x => x * factor), ScaleY = Map(item.Transform.ScaleY, y => y * factor) } };

    /// <summary>回転を degrees 度足す（時計回りが正）</summary>
    public static VisualItem Rotate(VisualItem item, double degrees) =>
        item with { Transform = item.Transform with { Rotation = Map(item.Transform.Rotation, r => r + degrees) } };

    /// <summary>a から b へ、pivot のまわりに回った角度（度、時計回りが正）</summary>
    public static double AngleBetween(Vector2 pivot, Vector2 a, Vector2 b)
    {
        var from = Math.Atan2(a.Y - pivot.Y, a.X - pivot.X);
        var to = Math.Atan2(b.Y - pivot.Y, b.X - pivot.X);
        var degrees = (to - from) * 180 / Math.PI;
        return degrees > 180 ? degrees - 360 : degrees < -180 ? degrees + 360 : degrees;
    }

    private static Animatable<double> Map(Animatable<double> value, Func<double, double> change) =>
        value.IsAnimated
            ? Animatable<double>.FromKeys(value.Keys.Select(key => key with { Value = change(key.Value) }))
            : Animatable<double>.Constant(change(value.Value));
}
