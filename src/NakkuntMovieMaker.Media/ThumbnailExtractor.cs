using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// 動画・画像からサムネイルを取り出す。タイムラインに並べる複数の時刻のサムネイルは、
/// 1 回開いたファイルを使い回して取り出す
/// </summary>
public static class ThumbnailExtractor
{
    /// <summary>指定した時刻（秒）に表示されているフレームを、幅 maxWidth・高さ maxHeight に収まる大きさで取り出す</summary>
    /// <exception cref="MediaException">ファイルを開けない、または映像が含まれていない</exception>
    public static BgraImage Extract(string path, Rational time, int maxWidth, int maxHeight) =>
        ExtractMany(path, [time], maxWidth, maxHeight)[0];

    /// <summary>複数の時刻のフレームを取り出す。時刻が素材の長さを超えるときは、最後のフレームを返す</summary>
    public static IReadOnlyList<BgraImage> ExtractMany(string path, IReadOnlyList<Rational> times, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);

        // たくさんのファイルを次々に開くので、準備に時間のかかるハードウェアデコードは使わない
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions(maxWidth, maxHeight, HardwareDecoding.Disabled, CacheCapacity: 1));
        return [.. times.Select(time => decoder.GetFrame(time).Bgra)];
    }
}
