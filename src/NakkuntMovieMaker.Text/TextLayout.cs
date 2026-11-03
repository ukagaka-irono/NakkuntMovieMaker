using System.Globalization;
using HarfBuzzSharp;
using NakkuntMovieMaker.Core.Model;
using SkiaSharp;

namespace NakkuntMovieMaker.Text;

/// <summary>
/// 文字の配置。HarfBuzz で文字の形と位置を決め（カーニング・合字・縦書き用の字形など）、
/// 行（縦書きでは列）を揃えて並べる。座標は文字の箱（Width × Height）の左上が原点。
/// 箱の大きさは、横書きなら「一番長い行の幅 × 行の数 × 行の高さ」、縦書きなら「列の数 × 行の高さ × 一番長い列の長さ」。
/// 縁取り・影・背景は箱の外にはみ出してよい（InkBounds に含む）。
/// 一文字ずつ表示するときも配置は全体で決めるので、文字が増えても位置は動かない
/// </summary>
public sealed class TextLayout
{
    private static readonly Language Japanese = new("ja");

    private readonly TextStyle _style;
    private readonly List<GlyphRun> _runs;

    private TextLayout(TextStyle style, List<GlyphRun> runs, float width, float height, int elementCount)
    {
        _style = style;
        _runs = runs;
        Width = width;
        Height = height;
        TextElementCount = elementCount;
        InkBounds = ComputeInkBounds();
    }

    /// <summary>文字の箱の幅</summary>
    public float Width { get; }

    /// <summary>文字の箱の高さ</summary>
    public float Height { get; }

    /// <summary>文字の数（絵文字などの組み合わせ文字は 1 文字と数える。改行も 1 文字）</summary>
    public int TextElementCount { get; }

    /// <summary>縁取り・影・背景を含めて、描かれる可能性のある範囲（多めに見積もる）</summary>
    public SKRect InkBounds { get; }

    public static TextLayout Create(string text, TextStyle style, FontProvider? fonts = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        fonts ??= FontProvider.Default;

        var size = (float)Math.Max(style.FontSize, 0);
        var lineHeight = (float)(size * style.LineSpacing);
        var primary = fonts.Resolve(style.FontFamily, style.Bold, style.Italic);
        using var metricsFont = primary.CreateFont(size);
        var metrics = metricsFont.Metrics;

        // UTF-16 の位置 → 文字（text element）の番号
        var starts = StringInfo.ParseCombiningCharacters(text);
        var elementAt = new int[text.Length + 1];
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            Array.Fill(elementAt, i, starts[i], end - starts[i]);
        }

        elementAt[text.Length] = starts.Length;

        var lines = SplitLines(text);
        var shaped = lines
            .Select(line => ShapeLine(text, line, starts, elementAt, primary, fonts, size, (float)style.LetterSpacing, style.Vertical))
            .ToList();

        var longest = shaped.Count == 0 ? 0 : shaped.Max(line => line.Length);
        var runs = new List<GlyphRun>();
        float width;
        float height;
        if (!style.Vertical)
        {
            width = longest;
            height = lineHeight * shaped.Count;

            // 行の高さの中で、文字（アセント＋ディセント）が上下の真ん中に来るようにする
            var baseline = (lineHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
            for (var i = 0; i < shaped.Count; i++)
            {
                var x = Align(style.Alignment, width - shaped[i].Length);
                runs.AddRange(shaped[i].Runs.Select(run => run.Offset(x, i * lineHeight + baseline)));
            }
        }
        else
        {
            // 縦書き：列は右から左へ並べ、文字は列の中心に置く
            width = lineHeight * shaped.Count;
            height = longest;
            for (var i = 0; i < shaped.Count; i++)
            {
                var y = Align(style.Alignment, height - shaped[i].Length);
                runs.AddRange(shaped[i].Runs.Select(run => run.Offset(width - (i + 0.5f) * lineHeight, y)));
            }
        }

        return new TextLayout(style, runs, width, height, starts.Length);
    }

    /// <summary>
    /// 文字を描く（箱の左上が canvas の原点）。visibleCharacters を指定すると、その数の文字だけを描く。
    /// 描く順番は、背景 → 影 → 縁取り → 文字
    /// </summary>
    public void Draw(SKCanvas canvas, int? visibleCharacters = null)
    {
        var visible = visibleCharacters ?? int.MaxValue;
        var blobs = BuildBlobs(visible);
        try
        {
            if (_style.Background is { } background)
            {
                using var paint = new SKPaint { Color = ToSkia(background.Color), IsAntialias = true };
                var padding = (float)background.Padding;
                var radius = (float)background.CornerRadius;
                canvas.DrawRoundRect(new SKRect(-padding, -padding, Width + padding, Height + padding), radius, radius, paint);
            }

            if (blobs.Count == 0)
            {
                return;
            }

            if (_style.Shadow is { } shadow)
            {
                using var paint = new SKPaint { Color = ToSkia(shadow.Color), IsAntialias = true };
                if (shadow.Blur > 0)
                {
                    paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)shadow.Blur);
                }

                // 影は、縁取りを含めた文字の形で落とす
                canvas.Save();
                canvas.Translate((float)shadow.OffsetX, (float)shadow.OffsetY);
                if (_style.Outline is { Width: > 0 } shadowOutline)
                {
                    paint.Style = SKPaintStyle.StrokeAndFill;
                    paint.StrokeWidth = (float)shadowOutline.Width * 2;
                    paint.StrokeJoin = SKStrokeJoin.Round;
                }

                DrawBlobs(canvas, blobs, paint);
                canvas.Restore();
            }

            if (_style.Outline is { Width: > 0 } outline)
            {
                // 線は輪郭の内と外に半分ずつ引かれるので、外側に Width 出るように 2 倍の太さで引く
                using var paint = new SKPaint
                {
                    Color = ToSkia(outline.Color),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)outline.Width * 2,
                    StrokeJoin = SKStrokeJoin.Round,
                };
                DrawBlobs(canvas, blobs, paint);
            }

            using var fill = new SKPaint { Color = ToSkia(_style.Color), IsAntialias = true };
            DrawBlobs(canvas, blobs, fill);
        }
        finally
        {
            foreach (var blob in blobs)
            {
                blob.Dispose();
            }
        }
    }

    private List<SKTextBlob> BuildBlobs(int visible)
    {
        var blobs = new List<SKTextBlob>();
        foreach (var run in _runs)
        {
            // 文字の番号は run の中で増えていくとは限らない（右から左の文字など）ので、見える字形だけを集める
            var glyphs = new List<ushort>();
            var positions = new List<SKPoint>();
            for (var i = 0; i < run.Glyphs.Length; i++)
            {
                if (run.Elements[i] < visible)
                {
                    glyphs.Add(run.Glyphs[i]);
                    positions.Add(run.Positions[i]);
                }
            }

            if (glyphs.Count == 0)
            {
                continue;
            }

            using var font = run.Face.CreateFont(run.Size);
            using var builder = new SKTextBlobBuilder();
            builder.AddPositionedRun(glyphs.ToArray(), font, positions.ToArray());
            if (builder.Build() is { } blob)
            {
                blobs.Add(blob);
            }
        }

        return blobs;
    }

    private static void DrawBlobs(SKCanvas canvas, List<SKTextBlob> blobs, SKPaint paint)
    {
        foreach (var blob in blobs)
        {
            canvas.DrawText(blob, 0, 0, paint);
        }
    }

    private SKRect ComputeInkBounds()
    {
        var size = (float)_style.FontSize;
        var outline = (float)(_style.Outline?.Width ?? 0);
        var bounds = new SKRect(0, 0, Width, Height);
        bounds.Inflate(size * 0.5f + outline, size * 0.5f + outline);
        if (_style.Shadow is { } shadow)
        {
            var shadowBounds = bounds;
            shadowBounds.Offset((float)shadow.OffsetX, (float)shadow.OffsetY);
            shadowBounds.Inflate((float)shadow.Blur * 3, (float)shadow.Blur * 3);
            bounds.Union(shadowBounds);
        }

        if (_style.Background is { } background)
        {
            var padding = (float)background.Padding;
            bounds.Union(new SKRect(-padding, -padding, Width + padding, Height + padding));
        }

        return bounds;
    }

    private static float Align(TextAlignment alignment, float space) => alignment switch
    {
        TextAlignment.Left => 0,
        TextAlignment.Right => space,
        _ => space / 2,
    };

    // 改行（\n・\r\n・\r）で分けた各行の [開始, 終わり) の UTF-16 の位置
    private static List<(int Start, int End)> SplitLines(string text)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\n' or '\r')
            {
                lines.Add((start, i));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        lines.Add((start, text.Length));
        return lines;
    }

    // 1 行（縦書きでは 1 列）を配置する。字形の位置は、行の先頭・ベースライン（縦書きでは列の中心・上端）が原点
    private static ShapedLine ShapeLine(
        string text,
        (int Start, int End) line,
        int[] starts,
        int[] elementAt,
        FontFace primary,
        FontProvider fonts,
        float size,
        float letterSpacing,
        bool vertical)
    {
        var runs = new List<GlyphRun>();
        var pen = 0f;
        var firstElement = elementAt[line.Start];
        foreach (var (segmentStart, segmentEnd, face) in Segment(text, line, starts, elementAt, primary, fonts))
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(text, segmentStart, segmentEnd - segmentStart);
            buffer.GuessSegmentProperties();
            buffer.Language = Japanese;
            buffer.Direction = vertical ? Direction.TopToBottom : Direction.LeftToRight;
            face.Shaper.Shape(buffer);

            var infos = buffer.GetGlyphInfoSpan();
            var positions = buffer.GetGlyphPositionSpan();
            var scale = size / face.UnitsPerEm;
            var glyphs = new ushort[infos.Length];
            var points = new SKPoint[infos.Length];
            var elements = new int[infos.Length];
            for (var i = 0; i < infos.Length; i++)
            {
                var glyph = infos[i].Codepoint;
                var element = elementAt[(int)infos[i].Cluster];
                glyphs[i] = (ushort)glyph;
                elements[i] = element;

                // 字間は、その文字より前の文字の数だけ足す
                var spacing = letterSpacing * (element - firstElement);
                if (!vertical)
                {
                    points[i] = new SKPoint(pen + spacing + positions[i].XOffset * scale, -positions[i].YOffset * scale);
                    pen += positions[i].XAdvance * scale;
                }
                else
                {
                    // HarfBuzz は縦書きのずれ（オフセット）に、縦の原点から横の原点までの差を含めて返す（y は上向き）。
                    // そのまま横の原点として Skia で描ける
                    points[i] = new SKPoint(
                        positions[i].XOffset * scale,
                        pen + spacing - positions[i].YOffset * scale);
                    pen -= positions[i].YAdvance * scale;
                }
            }

            runs.Add(new GlyphRun(face, size, glyphs, points, elements));
        }

        var elementCount = elementAt[line.End] - firstElement;
        var length = pen + letterSpacing * Math.Max(0, elementCount - 1);
        return new ShapedLine(runs, length);
    }

    // 行を、使うフォントが同じ文字の並びに分ける（同梱のフォントにない文字は、代わりのフォントで描く）
    private static IEnumerable<(int Start, int End, FontFace Face)> Segment(
        string text, (int Start, int End) line, int[] starts, int[] elementAt, FontFace primary, FontProvider fonts)
    {
        var segmentStart = line.Start;
        FontFace? current = null;
        for (var element = elementAt[line.Start]; element < elementAt[line.End]; element++)
        {
            var start = starts[element];
            var codepoint = char.IsSurrogatePair(text, start) ? char.ConvertToUtf32(text, start) : text[start];
            var face = primary.HasGlyph(codepoint) || char.IsWhiteSpace(text, start)
                ? primary
                : fonts.Fallback(primary, codepoint) ?? primary;
            if (current is not null && face != current)
            {
                yield return (segmentStart, start, current);
                segmentStart = start;
            }

            current = face;
        }

        if (current is not null)
        {
            yield return (segmentStart, line.End, current);
        }
    }

    private static SKColor ToSkia(RgbaColor color) => new(color.R, color.G, color.B, color.A);

    private sealed record ShapedLine(List<GlyphRun> Runs, float Length);

    private sealed record GlyphRun(FontFace Face, float Size, ushort[] Glyphs, SKPoint[] Positions, int[] Elements)
    {
        public GlyphRun Offset(float x, float y) =>
            this with { Positions = [.. Positions.Select(point => new SKPoint(point.X + x, point.Y + y))] };
    }
}
