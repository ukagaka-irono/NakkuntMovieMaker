using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.Services;

/// <summary>エンジンの画像（BGRA・premultiplied）を Avalonia のビットマップにする</summary>
public static class Bitmaps
{
    public static WriteableBitmap Create(BgraImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        CopyInto(bitmap, image);
        return bitmap;
    }

    /// <summary>同じ大きさのビットマップに画素を写す（大きさが違えば false）</summary>
    public static bool CopyInto(WriteableBitmap bitmap, BgraImage image)
    {
        if (bitmap.PixelSize.Width != image.Width || bitmap.PixelSize.Height != image.Height)
        {
            return false;
        }

        using var buffer = bitmap.Lock();
        for (var y = 0; y < image.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(image.Pixels, y * image.Stride, buffer.Address + y * buffer.RowBytes, image.Stride);
        }

        return true;
    }
}
