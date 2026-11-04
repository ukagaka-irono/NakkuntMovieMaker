namespace NakkuntMovieMaker.Media;

/// <summary>動画ファイル（MP4）を書くもの。映像は BGRA の画像を 1 枚ずつ、音声はステレオの float32 を順に渡し、最後に Finish を呼ぶ</summary>
public interface IMediaFileWriter : IDisposable
{
    /// <summary>使っている映像のエンコーダーの名前（"h264_qsv" など）</summary>
    string? VideoEncoder { get; }

    /// <summary>映像の 1 フレーム（premultiplied の BGRA）を書く。大きさは設定と同じにすること</summary>
    void WriteVideo(BgraImage image);

    /// <summary>音声（ステレオのインターリーブ、-1〜1）を書く</summary>
    void WriteAudio(ReadOnlySpan<float> stereo);

    /// <summary>残っている分を出し切って、ファイルを閉じる</summary>
    void Finish();
}
