namespace NakkuntMovieMaker.Core.Model;

/// <summary>文字の揃え</summary>
public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 文字のスタイル。FontFamily が空のときは既定のフォントを使う。
/// LineSpacing は行の高さの倍率、LetterSpacing は字間のピクセル、
/// RevealCharsPerSecond は一文字ずつ表示する速さ（0 なら一度に全部表示）
/// </summary>
public sealed record TextStyle(
    string FontFamily,
    double FontSize,
    RgbaColor Color,
    bool Bold = false,
    bool Italic = false,
    TextOutline? Outline = null,
    TextShadow? Shadow = null,
    TextBackground? Background = null,
    TextAlignment Alignment = TextAlignment.Center,
    double LineSpacing = 1.2,
    double LetterSpacing = 0,
    bool Vertical = false,
    double RevealCharsPerSecond = 0)
{
    public static readonly TextStyle Default = new("", 64, RgbaColor.White, Outline: new TextOutline(RgbaColor.Black, 4));
}

/// <summary>文字の縁取り</summary>
public sealed record TextOutline(RgbaColor Color, double Width);

/// <summary>文字の影</summary>
public sealed record TextShadow(RgbaColor Color, double OffsetX, double OffsetY, double Blur = 0);

/// <summary>文字の背景（文字を囲む四角）</summary>
public sealed record TextBackground(RgbaColor Color, double Padding = 8, double CornerRadius = 0);

/// <summary>図形の種類</summary>
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,

    /// <summary>正多角形（Sides で角の数を指定）</summary>
    Polygon,

    /// <summary>星形（Sides で頂点の数、InnerRadiusRatio でへこみの深さを指定）</summary>
    Star,

    Line,
    Arrow,

    /// <summary>画面全体を塗る背景</summary>
    Background,
}

/// <summary>
/// 図形。Width・Height はピクセル。CornerRadius を指定すると角丸の四角形になる
/// </summary>
public sealed record ShapeStyle(
    ShapeKind Kind,
    double Width,
    double Height,
    Paint Fill,
    RgbaColor? StrokeColor = null,
    double StrokeWidth = 0,
    double CornerRadius = 0,
    int Sides = 5,
    double InnerRadiusRatio = 0.5);

/// <summary>塗り</summary>
public abstract record Paint;

/// <summary>単色の塗り</summary>
public sealed record SolidPaint(RgbaColor Color) : Paint;

/// <summary>2 色の直線グラデーション。Angle は度（0 で左から右）</summary>
public sealed record LinearGradientPaint(RgbaColor Start, RgbaColor End, double Angle = 0) : Paint;
