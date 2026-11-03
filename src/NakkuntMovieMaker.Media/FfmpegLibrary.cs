using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace NakkuntMovieMaker.Media;

/// <summary>読み込んだ FFmpeg の情報</summary>
public sealed record FfmpegInfo(
    string Version,
    string AvcodecVersion,
    string AvformatVersion,
    string AvutilVersion,
    string License,
    string Configuration)
{
    /// <summary>LGPL の構成か（GPL の部品を含んでいないか）</summary>
    public bool IsLgpl => License.StartsWith("LGPL", StringComparison.Ordinal);
}

/// <summary>FFmpeg の共有ライブラリを読み込む</summary>
public static class FfmpegLibrary
{
    /// <summary>FFmpeg の場所を指定する環境変数。指定がなければアプリのフォルダから探す</summary>
    public const string DirectoryEnvironmentVariable = "NMM_FFMPEG_DIR";

    private static readonly Lock Gate = new();
    private static FfmpegInfo? _info;

    /// <summary>FFmpeg を読み込む。2 回目以降は最初に読み込んだ情報を返す</summary>
    /// <exception cref="InvalidOperationException">FFmpeg を読み込めなかった</exception>
    public static FfmpegInfo Load()
    {
        lock (Gate)
        {
            if (_info is not null)
            {
                return _info;
            }

            var directory = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (string.IsNullOrEmpty(directory))
            {
                directory = AppContext.BaseDirectory;
            }

            try
            {
                DynamicallyLoadedBindings.LibrariesPath = directory;
                DynamicallyLoadedBindings.Initialize();

                // 進み具合などの情報は出さず、エラーだけを標準エラーに出す
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

                _info = new FfmpegInfo(
                    ffmpeg.av_version_info(),
                    FormatVersion(ffmpeg.avcodec_version()),
                    FormatVersion(ffmpeg.avformat_version()),
                    FormatVersion(ffmpeg.avutil_version()),
                    ffmpeg.avcodec_license(),
                    ffmpeg.avcodec_configuration());
                return _info;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
            {
                throw new InvalidOperationException($"FFmpeg を読み込めませんでした（場所: {directory}）", ex);
            }
        }
    }

    /// <summary>FFmpeg を読み込む。読み込めなかった場合は false を返す</summary>
    public static bool TryLoad(out FfmpegInfo? info, out string? error)
    {
        try
        {
            info = Load();
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

    // FFmpeg のバージョン番号は (major << 16) | (minor << 8) | micro の形
    private static string FormatVersion(uint version) =>
        $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";
}
