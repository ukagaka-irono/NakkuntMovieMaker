using System.Globalization;

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
