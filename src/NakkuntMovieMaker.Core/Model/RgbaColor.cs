using System.Globalization;

namespace NakkuntMovieMaker.Core.Model;

/// <summary>sRGB の色（8 ビット、straight alpha）</summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);
    public static readonly RgbaColor Black = new(0, 0, 0);
    public static readonly RgbaColor White = new(255, 255, 255);

    /// <summary>"#RRGGBB" か "#RRGGBBAA" を読む</summary>
    public static RgbaColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryParse(text, out var color))
        {
            throw new FormatException($"色として読めません: {text}");
        }

        return color;
    }

    public static bool TryParse(string? text, out RgbaColor color)
    {
        color = default;
        if (text is null || text.Length is not (7 or 9) || text[0] != '#')
        {
            return false;
        }

        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = text.Length == 7
            ? new RgbaColor((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new RgbaColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>不透明なら "#RRGGBB"、そうでなければ "#RRGGBBAA"</summary>
    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}
