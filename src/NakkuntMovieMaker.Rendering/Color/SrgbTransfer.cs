namespace NakkuntMovieMaker.Rendering.Color;

/// <summary>sRGB の伝達関数。負の値は、OpenColorIO の sRGB と同じく、0 の近くの直線をそのまま延ばす</summary>
public static class SrgbTransfer
{
    /// <summary>sRGB の値 → リニア</summary>
    public static float Decode(float value)
    {
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>リニア → sRGB の値</summary>
    public static float Encode(float value)
    {
        return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f;
    }
}
