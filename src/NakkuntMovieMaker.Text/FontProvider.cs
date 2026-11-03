using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace NakkuntMovieMaker.Text;

/// <summary>
/// 文字に使うフォントを探す。フォントの指定がないとき・見つからないときは、同梱のフォント（BIZ UDPゴシック）を使う。
/// 同梱のフォントにない文字（絵文字など）は、OS のフォントから代わりを探す。
/// 読み込んだフォントは覚えておき、複数のスレッドから同時に使ってよい
/// </summary>
public sealed class FontProvider
{
    /// <summary>同梱のフォントのファミリー名</summary>
    public const string BundledFamily = "BIZ UDPGothic";

    private readonly Dictionary<(string Family, bool Bold), string> _bundledFiles = new();
    private readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), FontFace> _faces = new();
    private readonly ConcurrentDictionary<(FontFace Primary, int Codepoint), FontFace?> _fallbacks = new();

    /// <param name="bundledDirectory">同梱のフォント（.ttf・.otf）を置いたフォルダ</param>
    public FontProvider(string bundledDirectory)
    {
        if (!Directory.Exists(bundledDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(bundledDirectory).Where(IsFontFile).Order(StringComparer.Ordinal))
        {
            using var typeface = SKTypeface.FromFile(path);
            if (typeface is not null)
            {
                _bundledFiles.TryAdd((typeface.FamilyName, typeface.FontWeight >= (int)SKFontStyleWeight.SemiBold), path);
            }
        }
    }

    /// <summary>アプリのフォルダの fonts にある同梱のフォントを使う</summary>
    public static FontProvider Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "fonts"));

    /// <summary>同梱のフォントがあるか（ないときは OS の既定のフォントを使う）</summary>
    public bool HasBundledFont => _bundledFiles.ContainsKey((BundledFamily, false));

    /// <summary>ファミリー名と太字・斜体から、使うフォントを決める</summary>
    internal FontFace Resolve(string family, bool bold, bool italic) =>
        _faces.GetOrAdd((family, bold, italic), key => Load(key.Family, key.Bold, key.Italic));

    /// <summary>primary にない文字の代わりのフォント。見つからなければ null</summary>
    internal FontFace? Fallback(FontFace primary, int codepoint) =>
        _fallbacks.GetOrAdd((primary, codepoint), key =>
        {
            var style = new SKFontStyle(
                key.Primary.WantsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                key.Primary.WantsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var typeface = SKFontManager.Default.MatchCharacter(key.Primary.Typeface.FamilyName, style, ["ja"], key.Codepoint);
            if (typeface is null)
            {
                return null;
            }

            // 同じフォントは 1 つにまとめる
            return _faces.GetOrAdd(
                ($"fallback:{typeface.FamilyName}:{typeface.FontWeight}:{typeface.FontSlant}", key.Primary.WantsBold, key.Primary.WantsItalic),
                _ => new FontFace(typeface, key.Primary.WantsBold, key.Primary.WantsItalic));
        });

    private FontFace Load(string family, bool bold, bool italic)
    {
        var name = string.IsNullOrWhiteSpace(family) ? BundledFamily : family;
        if (TryLoadBundled(name, bold, italic) is { } bundled)
        {
            return bundled;
        }

        var style = new SKFontStyle(
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var system = SKFontManager.Default.MatchFamily(name, style);
        if (system is not null)
        {
            return new FontFace(system, bold, italic);
        }

        // 見つからないフォントは、同梱のフォント（なければ OS の既定のフォント）で代わりに描く
        return TryLoadBundled(BundledFamily, bold, italic)
            ?? new FontFace(SKTypeface.Default, bold, italic);
    }

    private FontFace? TryLoadBundled(string family, bool bold, bool italic)
    {
        // 太字のファイルがなければ、標準のファイルを太くして使う
        var path = _bundledFiles.GetValueOrDefault((family, bold)) ?? _bundledFiles.GetValueOrDefault((family, false));
        return path is not null && SKTypeface.FromFile(path) is { } typeface ? new FontFace(typeface, bold, italic) : null;
    }

    private static bool IsFontFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc";
}

/// <summary>
/// 描画用の Skia のフォントと、文字の配置（シェーピング）用の HarfBuzz のフォントの組。
/// 太字・斜体のフォントがないときは、太くする・傾ける処理で代わりにする
/// </summary>
internal sealed class FontFace
{
    // HarfBuzz はフォントのデータを使い続けるので、Blob・Face を持ち続ける
    // （持っていないと、ガベージコレクションで先に解放され、字送りがおかしくなることがある）
    private readonly HarfBuzzSharp.Blob _blob;
    private readonly HarfBuzzSharp.Face _face;

    public FontFace(SKTypeface typeface, bool bold, bool italic)
    {
        Typeface = typeface;
        WantsBold = bold;
        WantsItalic = italic;
        SyntheticBold = bold && typeface.FontWeight < (int)SKFontStyleWeight.SemiBold;
        SyntheticItalic = italic && typeface.FontSlant == SKFontStyleSlant.Upright;

        using var stream = typeface.OpenStream(out var index);
        using var data = SKData.Create(stream);
        var bytes = data.ToArray();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        _blob = new HarfBuzzSharp.Blob(handle.AddrOfPinnedObject(), bytes.Length, HarfBuzzSharp.MemoryMode.ReadOnly, () => handle.Free());
        _blob.MakeImmutable();
        _face = new HarfBuzzSharp.Face(_blob, index);
        _face.MakeImmutable();
        UnitsPerEm = _face.UnitsPerEm;
        Shaper = new HarfBuzzSharp.Font(_face);
        Shaper.SetFunctionsOpenType();
    }

    public SKTypeface Typeface { get; }

    public HarfBuzzSharp.Font Shaper { get; }

    public int UnitsPerEm { get; }

    public bool WantsBold { get; }

    public bool WantsItalic { get; }

    public bool SyntheticBold { get; }

    public bool SyntheticItalic { get; }

    public bool HasGlyph(int codepoint) => Typeface.ContainsGlyph(codepoint);

    /// <summary>この大きさで描くための Skia のフォント。環境で結果が変わらないよう、ヒンティングはしない</summary>
    public SKFont CreateFont(float size) => new(Typeface, size)
    {
        Embolden = SyntheticBold,
        SkewX = SyntheticItalic ? -0.2f : 0,
        Subpixel = true,
        Hinting = SKFontHinting.None,
        Edging = SKFontEdging.Antialias,
        LinearMetrics = true,
    };
}
