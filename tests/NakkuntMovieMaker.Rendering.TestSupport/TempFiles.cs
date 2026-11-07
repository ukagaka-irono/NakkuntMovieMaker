using System.Globalization;
using NakkuntMovieMaker.Rendering.Color;

namespace NakkuntMovieMaker.Rendering.TestSupport;

/// <summary>テスト用の一時フォルダ（破棄すると消す）</summary>
public sealed class TempFiles : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public TempFiles() => Directory.CreateDirectory(_path);

    public string Combine(string name) => Path.Combine(_path, name);

    public string Write(string name, string content)
    {
        var path = Combine(name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_path, recursive: true);
}

/// <summary>テスト用の LUT</summary>
public static class TestLuts
{
    /// <summary>
    /// 出力の変換のテスト用の ColorLut（OpenColorIO を使わずに作る）。暖色に寄せ、少しコントラストを付け、チャンネルを少し混ぜる。
    /// シェーパーは何もしない（u = x）
    /// </summary>
    public static ColorLut Grade { get; } = CreateGrade();

    private static ColorLut CreateGrade()
    {
        const int ShaperSize = 256;
        var shaper = new float[ShaperSize];
        for (var i = 0; i < ShaperSize; i++)
        {
            var t = i / (float)(ShaperSize - 1);
            shaper[i] = t * t;
        }

        const int Size = 17;
        var table = new System.Numerics.Vector3[Size * Size * Size];
        for (int b = 0, i = 0; b < Size; b++)
        {
            for (var g = 0; g < Size; g++)
            {
                for (var r = 0; r < Size; r++, i++)
                {
                    var (x, y, z) = (r / (Size - 1f), g / (Size - 1f), b / (Size - 1f));
                    static float Contrast(float v) => v + 0.15f * MathF.Sin(v * 2 * MathF.PI) / (2 * MathF.PI) * -1;
                    table[i] = new System.Numerics.Vector3(
                        Contrast(MathF.Pow(x, 0.9f)) * 0.95f + y * 0.05f,
                        Contrast(y),
                        Contrast(z) * 0.85f + x * 0.05f);
                }
            }
        }

        return new ColorLut(shaper, CubeLut.FromTable(Size, table, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One), encodeSrgb: false);
    }

    /// <summary>size×size×size の .cube の中身（赤が一番速く変わる順）</summary>
    public static string Cube(int size, Func<(double R, double G, double B), (double R, double G, double B)> map)
    {
        var lines = new List<string> { "# テスト用の LUT", $"LUT_3D_SIZE {size}" };
        for (var b = 0; b < size; b++)
        {
            for (var g = 0; g < size; g++)
            {
                for (var r = 0; r < size; r++)
                {
                    var (mr, mg, mb) = map((r / (size - 1.0), g / (size - 1.0), b / (size - 1.0)));
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"{mr:F6} {mg:F6} {mb:F6}"));
                }
            }
        }

        return string.Join("\n", lines);
    }
}
