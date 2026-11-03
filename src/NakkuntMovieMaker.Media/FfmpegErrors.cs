using FFmpeg.AutoGen.Abstractions;

namespace NakkuntMovieMaker.Media;

/// <summary>メディアファイルを扱えなかった。Message は利用者に表示できる文</summary>
public sealed class MediaException(string message, Exception? innerException = null) : Exception(message, innerException);

internal static unsafe class FfmpegErrors
{
    /// <summary>FFmpeg の AVERROR(EAGAIN)</summary>
    public static readonly int TryAgain = -ffmpeg.EAGAIN;

    /// <summary>FFmpeg のエラーコードを文章にする</summary>
    public static string Describe(int error)
    {
        const int bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        return ffmpeg.av_strerror(error, buffer, bufferSize) == 0
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer) ?? $"エラー {error}"
            : $"エラー {error}";
    }

    /// <summary>負の値（エラー）なら MediaException を投げる</summary>
    public static int ThrowIfError(this int result, string what)
    {
        if (result < 0)
        {
            throw new MediaException($"{what}に失敗しました（{Describe(result)}）");
        }

        return result;
    }
}
