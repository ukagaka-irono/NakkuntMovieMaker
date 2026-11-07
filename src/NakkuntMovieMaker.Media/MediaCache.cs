using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// 素材から作るキャッシュ（音声の PCM・波形のピーク・画像の縮小版・動画のプロキシ）を、フォルダに保存して使い回す。
/// プロジェクトでは .nmm/cache を使う（Git では除外する）。
/// キャッシュの名前は素材の場所・大きさ・更新日時と設定から決めるので、素材を差し替えると作り直す。
/// ファイルは一時ファイルに書いてから名前を変えるので、途中で止まっても壊れたキャッシュは残らない。
/// 複数のスレッドから同時に使ってよい
/// </summary>
public sealed class MediaCache
{
    // キャッシュの中身の作り方を変えたら上げる（古いキャッシュは使われなくなる）
    private const int FormatVersion = 1;

    private readonly ConcurrentDictionary<string, Lock> _locks = new();

    public MediaCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    /// <summary>素材の音声を 48kHz・float32 の PCM キャッシュにして開く。キャッシュがなければ作る</summary>
    /// <exception cref="MediaException">ファイルを開けない、または音声が含まれていない</exception>
    public PcmAudio OpenAudio(string sourcePath, CancellationToken cancellationToken = default)
    {
        var path = CachePath("audio", sourcePath, ".pcm");
        return Use(path, temp => AudioConverter.Convert(sourcePath, temp, cancellationToken), PcmAudio.Open);
    }

    /// <summary>素材の音声の波形のピークを返す。キャッシュがなければ PCM キャッシュから作る</summary>
    /// <exception cref="MediaException">ファイルを開けない、または音声が含まれていない</exception>
    public AudioPeaks GetPeaks(string sourcePath, CancellationToken cancellationToken = default)
    {
        var path = CachePath("peaks", sourcePath, ".peak");
        return Use(
            path,
            temp =>
            {
                using var audio = OpenAudio(sourcePath, cancellationToken);
                var peaks = AudioPeaks.Build(audio, cancellationToken);
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                peaks.Save(stream);
            },
            file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return AudioPeaks.Load(stream);
            });
    }

    /// <summary>
    /// 画像を、幅 maxWidth・高さ maxHeight に収まる大きさに縮小して返す（拡大はしない）。キャッシュがなければ作る。
    /// 大きな写真を毎回デコードして縮小する手間を省く
    /// </summary>
    /// <exception cref="MediaException">ファイルを開けない、または画像・映像ではない</exception>
    public BgraImage GetImage(string sourcePath, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        var path = CachePath("images", sourcePath, ".bgra", $"{maxWidth}x{maxHeight}");
        return Use(
            path,
            temp =>
            {
                using var decoder = VideoDecoder.Open(sourcePath, new VideoDecoderOptions(maxWidth, maxHeight, HardwareDecoding.Disabled, CacheCapacity: 1));
                var image = decoder.GetFrame(Rational.Zero).Image;
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                ImageFile.Save(stream, image);
            },
            ImageFile.Load);
    }

    /// <summary>
    /// 動画の素材のプロキシ（ProxyGenerator を参照）の場所を返す。キャッシュがなければ作る（素材の長さに応じて時間がかかる）。
    /// 同じ素材・設定のプロキシを別のスレッドが作っているときは、できるまで待つ
    /// </summary>
    /// <exception cref="MediaException">ファイルを開けない、映像が含まれていない、または使えるエンコーダーがない</exception>
    /// <exception cref="OperationCanceledException">キャンセルした（作りかけのファイルは残らない）</exception>
    public string GetProxy(
        string sourcePath,
        ProxySettings? settings = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= ProxySettings.Default;
        var path = CachePath("proxies", sourcePath, ".mp4", settings.CacheKey);
        return Use(path, temp => ProxyGenerator.Generate(sourcePath, temp, settings, progress, cancellationToken), file => file);
    }

    /// <summary>素材のプロキシがもう作ってあれば、その場所を返す。なければ（素材が見つからなければ）null。作りはしない</summary>
    public string? FindProxy(string sourcePath, ProxySettings? settings = null)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        try
        {
            var path = CachePath("proxies", sourcePath, ".mp4", (settings ?? ProxySettings.Default).CacheKey);
            return File.Exists(path) ? path : null;
        }
        catch (MediaException)
        {
            return null;
        }
    }

    // キャッシュがあれば開き、なければ作ってから開く。開けない（壊れている・古い）キャッシュは作り直す
    private T Use<T>(string path, Action<string> create, Func<string, T> open)
    {
        if (File.Exists(path))
        {
            try
            {
                return open(path);
            }
            catch (InvalidDataException)
            {
                TryDelete(path);
            }
        }

        lock (_locks.GetOrAdd(path, _ => new Lock()))
        {
            if (!File.Exists(path))
            {
                Create(path, create);
            }
        }

        return open(path);
    }

    private static void Create(string path, Action<string> create)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            create(temp);
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // ほかのプロセスが先に作った。そちらを使う
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // 素材の場所・大きさ・更新日時と設定から、キャッシュのファイルの場所を決める
    private string CachePath(string kind, string sourcePath, string extension, string settings = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            throw new MediaException($"ファイルが見つかりません: {sourcePath}");
        }

        var key = $"{kind}|{FormatVersion}|{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{settings}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Directory, kind, hash + extension);
    }
}

/// <summary>
/// 画像のキャッシュのファイルの形式。16 バイトのヘッダー（識別子 "NMMIMG" と 2 バイトの 0、幅と高さの int32）の後に、
/// BgraImage の画素がそのまま並ぶ
/// </summary>
internal static class ImageFile
{
    private const int HeaderSize = 16;

    private static ReadOnlySpan<byte> Magic => "NMMIMG\0\0"u8;

    public static void Save(Stream stream, BgraImage image)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], image.Height);
        stream.Write(header);
        stream.Write(image.Pixels);
    }

    /// <exception cref="InvalidDataException">画像のキャッシュの形式ではない、または途中で切れている</exception>
    public static BgraImage Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.Length < HeaderSize)
        {
            throw new InvalidDataException($"画像のキャッシュが途中で切れています: {path}");
        }

        stream.ReadExactly(header);
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (!header.StartsWith(Magic) || width <= 0 || height <= 0 || stream.Length != HeaderSize + (long)width * height * 4)
        {
            throw new InvalidDataException($"画像のキャッシュの形式ではないか、大きさが合いません: {path}");
        }

        var pixels = new byte[width * height * 4];
        stream.ReadExactly(pixels);
        return new BgraImage(width, height, pixels);
    }
}
