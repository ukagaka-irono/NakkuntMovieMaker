using NakkuntMovieMaker.Core.Model;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia;

/// <summary>
/// 図形を描く。図形は幅 × 高さの箱（左上が原点）に収まるように作り、塗りの後に線を引く。
/// 線は輪郭の内と外に半分ずつ引くので、箱から線の太さの半分だけはみ出す
/// </summary>
internal static class ShapePainter
{
    // 矢印の軸の太さ（高さに対する割合）
    private const float ArrowShaftRatio = 0.4f;

    public static SKRect InkBounds(ShapeStyle shape, float width, float height)
    {
        var bounds = new SKRect(0, 0, width, height);
        var stroke = (float)Math.Max(shape.StrokeWidth, 0);
        // 尖った角では、線が太さの半分より外に出る（マイター）ので多めに取る
        bounds.Inflate(stroke * 2, stroke * 2);
        return bounds;
    }

    public static void Draw(SKCanvas canvas, ShapeStyle shape, float width, float height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var path = CreatePath(shape, width, height);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        switch (shape.Fill)
        {
            case SolidPaint solid:
                fill.Color = ToSkia(solid.Color);
                break;
            case LinearGradientPaint gradient:
                fill.Shader = CreateGradient(gradient, width, height);
                break;
        }

        canvas.DrawPath(path, fill);

        if (shape.StrokeColor is { } strokeColor && shape.StrokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = ToSkia(strokeColor),
                StrokeWidth = (float)shape.StrokeWidth,
                StrokeJoin = SKStrokeJoin.Miter,
                StrokeMiter = 4,
            };
            canvas.DrawPath(path, stroke);
        }
    }

    private static SKPath CreatePath(ShapeStyle shape, float width, float height)
    {
        var path = new SKPath();
        var box = new SKRect(0, 0, width, height);
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle when shape.CornerRadius > 0:
                var radius = (float)Math.Min(shape.CornerRadius, Math.Min(width, height) / 2);
                path.AddRoundRect(box, radius, radius);
                break;
            case ShapeKind.Ellipse:
                path.AddOval(box);
                break;
            case ShapeKind.Triangle:
                path.AddPoly([new SKPoint(width / 2, 0), new SKPoint(width, height), new SKPoint(0, height)]);
                break;
            case ShapeKind.Polygon:
                path.AddPoly(RegularPolygon(Math.Max(3, shape.Sides), width, height, 1));
                break;
            case ShapeKind.Star:
                path.AddPoly(RegularPolygon(Math.Max(3, shape.Sides), width, height, (float)Math.Clamp(shape.InnerRadiusRatio, 0, 1)));
                break;
            case ShapeKind.Arrow:
                path.AddPoly(Arrow(width, height));
                break;

            // 四角形・線（幅 × 太さ）・背景は、箱をそのまま塗る
            default:
                path.AddRect(box);
                break;
        }

        return path;
    }

    // 箱に内接する楕円の上に頂点を置いた正多角形。innerRatio が 1 未満なら、頂点の間にへこみを入れた星形になる。
    // 最初の頂点は真上
    private static SKPoint[] RegularPolygon(int sides, float width, float height, float innerRatio)
    {
        var star = innerRatio < 1;
        var count = star ? sides * 2 : sides;
        var points = new SKPoint[count];
        for (var i = 0; i < count; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / count;
            var ratio = star && i % 2 == 1 ? innerRatio : 1;
            points[i] = new SKPoint(
                (float)(width / 2 + Math.Cos(angle) * width / 2 * ratio),
                (float)(height / 2 + Math.Sin(angle) * height / 2 * ratio));
        }

        return points;
    }

    // 右向きの矢印。頭の長さは高さと同じ（幅が足りなければ幅いっぱい）、軸の太さは高さの 4 割
    private static SKPoint[] Arrow(float width, float height)
    {
        var head = Math.Min(height, width);
        var shaftTop = height * (1 - ArrowShaftRatio) / 2;
        var shaftBottom = height - shaftTop;
        var neck = width - head;
        return
        [
            new(0, shaftTop), new(neck, shaftTop), new(neck, 0), new(width, height / 2),
            new(neck, height), new(neck, shaftBottom), new(0, shaftBottom),
        ];
    }

    // 角度の向き（0 度で左から右、時計回り）に、箱の端から端までかかるグラデーション
    private static SKShader CreateGradient(LinearGradientPaint gradient, float width, float height)
    {
        var angle = gradient.Angle * Math.PI / 180;
        var (dx, dy) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
        var half = Math.Abs(width / 2 * dx) + Math.Abs(height / 2 * dy);
        var center = new SKPoint(width / 2, height / 2);
        return SKShader.CreateLinearGradient(
            new SKPoint(center.X - dx * half, center.Y - dy * half),
            new SKPoint(center.X + dx * half, center.Y + dy * half),
            [ToSkia(gradient.Start), ToSkia(gradient.End)],
            SKShaderTileMode.Clamp);
    }

    private static SKColor ToSkia(RgbaColor color) => new(color.R, color.G, color.B, color.A);
}
