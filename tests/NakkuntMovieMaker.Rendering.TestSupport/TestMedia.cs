using System.Collections.Concurrent;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.TestSupport;

/// <summary>テスト用の素材。画像はメモリ上に作り、動画は時刻から画像を作る関数で表す</summary>
public sealed class TestMedia : IMediaSource
{
    private readonly ConcurrentDictionary<AssetId, SourceImage> _images = new();
    private readonly ConcurrentDictionary<AssetId, Func<Rational, SourceImage>> _videos = new();
    private readonly ConcurrentDictionary<string, SourceImage> _files = new();

    /// <summary>素材を求められたときの倍率（最後のもの）</summary>
    public double LastScale { get; private set; }

    public AssetId AddImage(SourceImage image)
    {
        var id = AssetId.New();
        _images[id] = image;
        return id;
    }

    public AssetId AddImage(BgraImage image) => AddImage(new SourceImage(image));

    public AssetId AddVideo(Func<Rational, BgraImage> frameAt)
    {
        var id = AssetId.New();
        _videos[id] = time => new SourceImage(frameAt(time));
        return id;
    }

    public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale)
    {
        LastScale = scale;
        return _videos.TryGetValue(asset, out var frameAt) ? frameAt(time) : null;
    }

    public SourceImage? GetImage(AssetId asset, double scale)
    {
        LastScale = scale;
        return _images.GetValueOrDefault(asset);
    }

    /// <summary>ファイルの場所 path で読める画像として置く（立ち絵のパーツなど）</summary>
    public string AddFile(string path, BgraImage image)
    {
        _files[path] = new SourceImage(image);
        return path;
    }

    public SourceImage? GetImageFile(string path, double scale) => _files.GetValueOrDefault(path);
}

/// <summary>テスト用の画像を作る。色は (R, G, B, A) の straight alpha で指定し、premultiplied にして持つ</summary>
public static class TestImages
{
    public static BgraImage Solid(int width, int height, (byte R, byte G, byte B, byte A) color) =>
        Create(width, height, (_, _) => color);

    /// <summary>左半分が left、右半分が right</summary>
    public static BgraImage Halves(int width, int height, (byte R, byte G, byte B, byte A) left, (byte R, byte G, byte B, byte A) right) =>
        Create(width, height, (x, _) => x < width / 2 ? left : right);

    /// <summary>赤が右へ、緑が下へ増えるグラデーション（青は 128）</summary>
    public static BgraImage Gradient(int width, int height) =>
        Create(width, height, (x, y) => ((byte)(x * 255 / (width - 1)), (byte)(y * 255 / (height - 1)), 128, 255));

    /// <summary>左上・右上・左下・右下で色が違う画像。右下は半透明</summary>
    public static BgraImage Quadrants(int width, int height) =>
        Create(width, height, (x, y) => (x < width / 2, y < height / 2) switch
        {
            (true, true) => (255, 255, 255, 255),
            (false, true) => (255, 200, 0, 255),
            (true, false) => (0, 160, 255, 255),
            (false, false) => (255, 0, 160, 128),
        });

    /// <summary>幅 stripe ピクセルごとに first と second を交互に並べた縦じま</summary>
    public static BgraImage Stripes(int width, int height, int stripe, (byte R, byte G, byte B, byte A) first, (byte R, byte G, byte B, byte A) second) =>
        Create(width, height, (x, _) => x / stripe % 2 == 0 ? first : second);

    /// <summary>
    /// エフェクトを確かめるための画像。上半分は色相が左から右へ一周するグラデーション、
    /// 下半分の左 2/3 は黒から白へのグラデーション、右 1/3 は緑（クロマキー用）
    /// </summary>
    public static BgraImage Subject(int width, int height) =>
        Create(width, height, (x, y) =>
        {
            if (y < height / 2)
            {
                return Hue(x * 360.0 / width);
            }

            if (x < width * 2 / 3)
            {
                var level = (byte)(x * 255 / (width * 2 / 3 - 1));
                return (level, level, level, 255);
            }

            return (0, 255, 0, 255);
        });

    private static (byte R, byte G, byte B, byte A) Hue(double degrees)
    {
        double Channel(double offset)
        {
            var k = (offset + degrees / 60) % 6;
            return 1 - Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1));
        }

        return ((byte)Math.Round(Channel(5) * 255), (byte)Math.Round(Channel(3) * 255), (byte)Math.Round(Channel(1) * 255), 255);
    }

    public static BgraImage Create(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> colorAt)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = colorAt(x, y);
                var offset = (y * width + x) * 4;
                pixels[offset] = Premultiply(b, a);
                pixels[offset + 1] = Premultiply(g, a);
                pixels[offset + 2] = Premultiply(r, a);
                pixels[offset + 3] = a;
            }
        }

        return new BgraImage(width, height, pixels);
    }

    private static byte Premultiply(byte value, byte alpha) => (byte)((value * alpha + 127) / 255);
}
