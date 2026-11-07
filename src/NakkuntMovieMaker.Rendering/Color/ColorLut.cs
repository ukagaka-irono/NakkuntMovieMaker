using System.Numerics;

namespace NakkuntMovieMaker.Rendering.Color;

/// <summary>
/// 色の変換を焼いたもの（OpenColorIO の変換など）。入力は 0〜1 に収め、チャンネルごとにシェーパー（1D）で u（0〜1）にしてから、
/// u の格子の 3D LUT を三重線形補間で引き、EncodeSrgb なら sRGB の伝達関数をかけてから、0〜1 に収める（格子の値は収めずに持つ）。
/// OpenColorIO の変換を焼くときは、格子に出力の値をリニアに戻したもの（sRGB の伝達関数の逆）を入れ、EncodeSrgb にする
/// （出力の伝達関数の曲がりや、色域の外での折れ曲がりで、補間の誤差が大きくならないように）。
/// シェーパーは、√x を等間隔に並べた表（黒の近くほど細かい）で、出力が大きく変わるところに 3D LUT の格子が集まるようにする。
/// GPU も、同じ表を同じ順番・同じ補間で引く
/// </summary>
public sealed class ColorLut
{
    private readonly float[] _shaper;

    public ColorLut(float[] shaper, CubeLut cube, bool encodeSrgb)
    {
        EncodeSrgb = encodeSrgb;
        ArgumentNullException.ThrowIfNull(shaper);
        ArgumentNullException.ThrowIfNull(cube);
        ArgumentOutOfRangeException.ThrowIfLessThan(shaper.Length, 2);
        _shaper = shaper;
        Cube = cube;
    }

    /// <summary>シェーパー：要素 i は、入力 x = (i / (長さ − 1))² のときの u</summary>
    public ReadOnlySpan<float> Shaper => _shaper;

    /// <summary>u の格子の 3D LUT（入力の範囲は 0〜1）</summary>
    public CubeLut Cube { get; }

    /// <summary>3D LUT を引いたあとに、sRGB の伝達関数（リニア → sRGB）をかけるか</summary>
    public bool EncodeSrgb { get; }

    /// <summary>色を変換する（入力も出力も 0〜1 に収める）</summary>
    public Vector3 Sample(Vector3 rgb)
    {
        var value = Cube.Sample(new Vector3(Shape(rgb.X), Shape(rgb.Y), Shape(rgb.Z)));
        if (EncodeSrgb)
        {
            value = new Vector3(SrgbTransfer.Encode(value.X), SrgbTransfer.Encode(value.Y), SrgbTransfer.Encode(value.Z));
        }

        return Vector3.Clamp(value, Vector3.Zero, Vector3.One);
    }

    /// <summary>1 つのチャンネルの入力 x を u にする</summary>
    public float Shape(float x)
    {
        var position = MathF.Sqrt(Math.Clamp(x, 0, 1)) * (_shaper.Length - 1);
        var index = Math.Min((int)position, _shaper.Length - 2);
        var fraction = position - index;
        return _shaper[index] + (_shaper[index + 1] - _shaper[index]) * fraction;
    }
}
