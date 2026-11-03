using NakkuntMovieMaker.Media;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia;

/// <summary>描いたフレームを画像ファイルにする（静止画の書き出し）</summary>
public static class ImageFiles
{
    /// <summary>
    /// 拡張子（.png・.jpg・.jpeg・.webp）に合わせた形式で書く。quality は JPEG と WebP の画質（1〜100）
    /// </summary>
    /// <exception cref="ArgumentException">対応していない拡張子</exception>
    public static void Save(BgraImage image, string path, int quality = 95)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            var extension => throw new ArgumentException($"静止画として書き出せない形式です: {extension}", nameof(path)),
        };

        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var data = bitmap.Encode(format, Math.Clamp(quality, 1, 100));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
