using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

/// <summary>
/// .cube 形式の 3D LUT（Adobe・DaVinci Resolve などが書き出すもの）。色は三重線形補間で引く。
/// 読み込んだ LUT は、ファイルの場所と更新日時ごとに覚えておく
/// </summary>
internal sealed class CubeLut
{
    private static readonly ConcurrentDictionary<(string Path, DateTime Modified), CubeLut?> Cache = new();

    private readonly int _size;
    private readonly Vector3[] _table;
    private readonly Vector3 _domainMin;
    private readonly Vector3 _domainScale;

    private CubeLut(int size, Vector3[] table, Vector3 domainMin, Vector3 domainMax)
    {
        _size = size;
        _table = table;
        _domainMin = domainMin;
        var range = domainMax - domainMin;
        _domainScale = new Vector3(1 / range.X, 1 / range.Y, 1 / range.Z);
    }

    /// <summary>LUT を読む。ファイルがない・読めない・形式が違うときは null</summary>
    public static CubeLut? TryLoad(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return Cache.GetOrAdd((info.FullName, info.LastWriteTimeUtc), key => TryParse(File.ReadLines(key.Path)));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>.cube の中身を読む。3D LUT でない・数が足りないときは null</summary>
    public static CubeLut? TryParse(IEnumerable<string> lines)
    {
        var size = 0;
        var domainMin = Vector3.Zero;
        var domainMax = Vector3.One;
        var values = new List<Vector3>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToUpperInvariant())
            {
                case "TITLE":
                    break;
                case "LUT_3D_SIZE" when parts.Length >= 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parsed):
                    size = parsed;
                    break;
                case "LUT_1D_SIZE":
                    return null;
                case "DOMAIN_MIN" when TryVector(parts, 1, out var min):
                    domainMin = min;
                    break;
                case "DOMAIN_MAX" when TryVector(parts, 1, out var max):
                    domainMax = max;
                    break;
                default:
                    if (TryVector(parts, 0, out var value))
                    {
                        values.Add(value);
                    }

                    break;
            }
        }

        if (size < 2 || size > 256 || values.Count != size * size * size
            || domainMax.X <= domainMin.X || domainMax.Y <= domainMin.Y || domainMax.Z <= domainMin.Z)
        {
            return null;
        }

        return new CubeLut(size, [.. values], domainMin, domainMax);
    }

    /// <summary>色（0〜1）を LUT で変換する。.cube は赤が一番速く変わる順に並ぶ</summary>
    public Vector3 Sample(Vector3 rgb)
    {
        var position = Vector3.Clamp((rgb - _domainMin) * _domainScale, Vector3.Zero, Vector3.One) * (_size - 1);
        var r0 = Math.Min((int)position.X, _size - 2);
        var g0 = Math.Min((int)position.Y, _size - 2);
        var b0 = Math.Min((int)position.Z, _size - 2);
        var fr = position.X - r0;
        var fg = position.Y - g0;
        var fb = position.Z - b0;

        Vector3 At(int r, int g, int b) => _table[(b * _size + g) * _size + r];

        var c00 = Vector3.Lerp(At(r0, g0, b0), At(r0 + 1, g0, b0), fr);
        var c10 = Vector3.Lerp(At(r0, g0 + 1, b0), At(r0 + 1, g0 + 1, b0), fr);
        var c01 = Vector3.Lerp(At(r0, g0, b0 + 1), At(r0 + 1, g0, b0 + 1), fr);
        var c11 = Vector3.Lerp(At(r0, g0 + 1, b0 + 1), At(r0 + 1, g0 + 1, b0 + 1), fr);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, fg), Vector3.Lerp(c01, c11, fg), fb);
    }

    private static bool TryVector(string[] parts, int start, out Vector3 value)
    {
        value = default;
        if (parts.Length < start + 3
            || !float.TryParse(parts[start], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[start + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[start + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }
}
