using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Audio;

/// <summary>nmm_audio（miniaudio のラッパー）の情報</summary>
public sealed record NativeAudioInfo(int ApiVersion, string MiniaudioVersion);

/// <summary>nmm_audio を読み込む</summary>
public static partial class NativeAudio
{
    /// <summary>この .NET 側のコードが前提とする nmm_audio の API のバージョン</summary>
    public const int ExpectedApiVersion = 3;

    private const string LibraryName = "nmm_audio";

    /// <summary>nmm_audio の情報を取得する</summary>
    /// <exception cref="InvalidOperationException">nmm_audio を読み込めなかった、または API のバージョンが合わない</exception>
    public static NativeAudioInfo GetInfo()
    {
        try
        {
            var apiVersion = nmm_audio_api_version();
            if (apiVersion != ExpectedApiVersion)
            {
                throw new InvalidOperationException(
                    $"nmm_audio の API のバージョンが合いません（期待値 {ExpectedApiVersion}、実際 {apiVersion}）");
            }

            var miniaudioVersion = Marshal.PtrToStringUTF8(nmm_audio_miniaudio_version()) ?? "";
            return new NativeAudioInfo(apiVersion, miniaudioVersion);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("nmm_audio を読み込めませんでした", ex);
        }
    }

    /// <summary>nmm_audio の情報を取得する。読み込めなかった場合は false を返す</summary>
    public static bool TryGetInfo(out NativeAudioInfo? info, out string? error)
    {
        try
        {
            info = GetInfo();
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            info = null;
            error = ex.InnerException is null ? ex.Message : $"{ex.Message}: {ex.InnerException.Message}";
            return false;
        }
    }

    [LibraryImport(LibraryName)]
    private static partial int nmm_audio_api_version();

    [LibraryImport(LibraryName)]
    private static partial nint nmm_audio_miniaudio_version();
}
