using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace NakkuntMovieMaker.App.Controls;

/// <summary>
/// タイムラインのアイテム名のような、1 行の短い文字を速く描く。FormattedText は 1 つ作るたびに文字の配置（行の分割・シェーピング）を
/// 行うので、アイテムが何千もあると、スクロールで新しい名前が見えるたびに時間がかかる。ここでは文字ごとのグリフと幅を覚えておき、
/// グリフを並べるだけにする（合字やカーニングは使わない。グリフがないフォントの文字は、ほかのフォントに切り替える）
/// </summary>
internal sealed class GlyphLabels(Typeface typeface, double fontSize)
{
    private readonly Dictionary<int, (GlyphTypeface Face, ushort Glyph, double Advance)> _glyphs = [];
    private readonly Dictionary<string, Label> _labels = [];
    private readonly GlyphTypeface _primary = typeface.GlyphTypeface;

    /// <summary>1 行の文字。Runs のグリフは、左上を (0, 0) とした位置に並んでいる</summary>
    public sealed record Label(IReadOnlyList<GlyphRun> Runs, double Width, double Height);

    /// <summary>文字の幅と高さ（描かずに測る）</summary>
    public Label Get(string text)
    {
        if (!_labels.TryGetValue(text, out var label))
        {
            if (_labels.Count > 4000)
            {
                _labels.Clear();
            }

            label = Build(text);
            _labels[text] = label;
        }

        return label;
    }

    /// <summary>左上を origin にして描く</summary>
    public void Draw(DrawingContext context, IBrush brush, Label label, Point origin)
    {
        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            foreach (var run in label.Runs)
            {
                context.DrawGlyphRun(brush, run);
            }
        }
    }

    private Label Build(string text)
    {
        var metrics = _primary.Metrics;
        var scale = fontSize / metrics.DesignEmHeight;
        var baseline = -metrics.Ascent * scale;
        var height = (metrics.Descent - metrics.Ascent + metrics.LineGap) * scale;
        var runs = new List<GlyphRun>();
        var x = 0.0;
        var segmentStart = 0;
        GlyphTypeface? segmentFace = null;
        var infos = new List<GlyphInfo>();
        var segmentX = 0.0;

        void Flush(int end)
        {
            if (segmentFace is not null && infos.Count > 0)
            {
                runs.Add(new GlyphRun(segmentFace, fontSize, text.AsMemory(segmentStart, end - segmentStart), infos.ToArray(), new Point(segmentX, baseline)));
            }

            infos.Clear();
        }

        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var (face, glyph, advance) = Glyph(rune.Value);
            if (face != segmentFace)
            {
                Flush(index);
                segmentFace = face;
                segmentStart = index;
                segmentX = x;
            }

            infos.Add(new GlyphInfo(glyph, index - segmentStart, advance));
            x += advance;
            index += rune.Utf16SequenceLength;
        }

        Flush(text.Length);
        return new Label(runs, x, height);
    }

    // 文字のグリフと幅。最初のフォントになければ、その文字を持つフォントを探す
    private (GlyphTypeface Face, ushort Glyph, double Advance) Glyph(int codepoint)
    {
        if (_glyphs.TryGetValue(codepoint, out var found))
        {
            return found;
        }

        var face = _primary;
        if (!face.CharacterToGlyphMap.TryGetGlyph(codepoint, out var glyph) || glyph == 0)
        {
            if (FontManager.Current.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, CultureInfo.CurrentUICulture, out var fallback)
                && fallback.GlyphTypeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out var fallbackGlyph))
            {
                face = fallback.GlyphTypeface;
                glyph = fallbackGlyph;
            }
        }

        var advance = face.TryGetHorizontalGlyphAdvance(glyph, out var units) ? units * fontSize / face.Metrics.DesignEmHeight : fontSize / 2;
        found = (face, glyph, advance);
        _glyphs[codepoint] = found;
        return found;
    }
}
