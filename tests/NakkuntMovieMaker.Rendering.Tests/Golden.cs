using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Media;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Tests;

/// <summary>
/// ゴールデン画像（Golden/*.png）との比較。基準は、各チャンネルの差が 2 以下、かつ PSNR が 45dB 以上。
/// 文字の形の作り方（Windows は DirectWrite、macOS は Core Text、Linux は FreeType）や、CPU の命令（x64 と arm64）で
/// 結果が少し変わるので、OS ごとの画像（Golden/linux/*.png、Golden/macos/*.png）があれば、そちらと比べる。
/// OS ごとの画像は、同じ OS でも版によって文字の輪郭が数段階ずれるので、ほぼ同じ（PSNR が 60dB 以上）なら差が 2 を超えても通す。
/// ゴールデン画像がなければ作って失敗する（中身を目で確かめてからコミットする）。
/// NMM_UPDATE_GOLDEN=1 で実行すると、すべて作り直す
/// </summary>
internal static class Golden
{
    private const int MaxChannelDifference = 2;
    private const double MinPsnr = 45;
    private const double PlatformNearlyIdenticalPsnr = 60;

    public static void AssertMatches(BgraImage actual, string name, [CallerFilePath] string callerPath = "")
    {
        var folder = Path.Combine(Path.GetDirectoryName(callerPath)!, "Golden");
        var path = Path.Combine(folder, name + ".png");
        var platformGolden = false;
        if (PlatformFolder is { } platform && File.Exists(Path.Combine(folder, platform, name + ".png")))
        {
            path = Path.Combine(folder, platform, name + ".png");
            platformGolden = true;
        }

        if (Environment.GetEnvironmentVariable("NMM_UPDATE_GOLDEN") == "1")
        {
            Save(actual, path);
            return;
        }

        if (!File.Exists(path))
        {
            Save(actual, path);
            Assert.Fail($"ゴールデン画像がなかったので作りました。中身を確かめてから、もう一度実行してください: {path}");
        }

        var expected = Load(path);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));

        var maxDifference = 0;
        var squaredError = 0.0;
        for (var i = 0; i < actual.Pixels.Length; i++)
        {
            var difference = Math.Abs(actual.Pixels[i] - expected.Pixels[i]);
            maxDifference = Math.Max(maxDifference, difference);
            squaredError += difference * difference;
        }

        var psnr = squaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squaredError / actual.Pixels.Length));
        var matches = (maxDifference <= MaxChannelDifference && psnr >= MinPsnr) || (platformGolden && psnr >= PlatformNearlyIdenticalPsnr);
        if (!matches)
        {
            // 違いを確かめられるように、描いた結果を出力先に置く
            var actualPath = Path.Combine(AppContext.BaseDirectory, "golden-actual", PlatformFolder ?? "", name + ".png");
            Save(actual, actualPath);
            Assert.Fail($"ゴールデン画像と違います（最大の差 {maxDifference}、PSNR {psnr:F1}dB）: {path}\n描いた結果: {actualPath}");
        }
    }

    // OS ごとのゴールデン画像を置くフォルダの名前（Windows は Golden の直下を使うので null）
    private static string? PlatformFolder =>
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : null;

    public static void Save(BgraImage image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = ToBitmap(image);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    public static BgraImage Load(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"PNG を読めません: {path}");
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // PNG は straight alpha なので、premultiplied に変えて読む
            Assert.True(decoded.PeekPixels().ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes));
        }
        finally
        {
            handle.Free();
        }

        return new BgraImage(info.Width, info.Height, pixels);
    }

    private static SKBitmap ToBitmap(BgraImage image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        return bitmap;
    }
}
