using System.Text.Json;
using System.Text.Json.Serialization;

namespace NakkuntMovieMaker.App.Services;

/// <summary>
/// アプリの設定（プロジェクトではなく、利用者ごとの設定）。%APPDATA%/NakkuntMovieMaker/settings.json に保存する。
/// Shortcuts はショートカットの変更分（コマンドの ID → キー。空文字なら割り当てなし）
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<string> RecentProjects { get; set; } = [];

    public Dictionary<string, string> Shortcuts { get; set; } = [];

    /// <summary>自動保存の間隔（分）。0 なら自動保存しない</summary>
    public int AutosaveMinutes { get; set; } = 3;

    /// <summary>プレビューの解像度（1・0.5・0.25）</summary>
    public double PreviewScale { get; set; } = 0.5;

    /// <summary>描画の方式（"auto"・"vulkan"・"cpu"）。null なら自動</summary>
    public string? Renderer { get; set; }

    /// <summary>GPU 版で使う GPU の名前。null なら自動で選ぶ</summary>
    public string? RendererDevice { get; set; }

    /// <summary>GPU 版で、動画を Vulkan Video でデコードする（試験的。落ちることがある）</summary>
    public bool ExperimentalVulkanVideo { get; set; }

    /// <summary>GPU 版が使ってよいメモリ（MB）。null なら自動（GPU のメモリの半分・最大 1GB）</summary>
    public int? GpuMemoryLimitMegabytes { get; set; }

    /// <summary>
    /// プレビューを表示するモニターの色空間（OpenColorIO の構成の中の名前。"Display P3 - Display" など）。
    /// null なら変換しない（sRGB のモニターとして表示する）
    /// </summary>
    public string? PreviewColorSpace { get; set; }

    public string? OutputDevice { get; set; }

    public string? InputDevice { get; set; }

    /// <summary>録音の遅れの補正（ミリ秒）。測ったことがなければ null</summary>
    public double? RecordingLatencyMilliseconds { get; set; }

    public bool ShowGrid { get; set; }

    public bool ShowSafeArea { get; set; }

    /// <summary>プレビューでプロキシを使う（プロジェクトのプロキシの設定に合う動画に）</summary>
    public bool UseProxiesInPreview { get; set; } = true;

    public bool Snap { get; set; } = true;

    /// <summary>書き出しに使う外部の ffmpeg の実行ファイル（x264・x265 で書き出すとき。利用者が自分で入手したもの）。使わなければ null</summary>
    public string? ExternalFfmpegPath { get; set; }

    /// <summary>利用者が保存したテキストのプリセット</summary>
    public List<SavedTextPreset> TextPresets { get; set; } = [];

    /// <summary>設定ファイルの場所</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NakkuntMovieMaker", "settings.json");

    /// <summary>設定を読む。ファイルがない・壊れているときは既定の設定</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>設定を書く。書けなくてもアプリは止めない</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>最近使ったプロジェクトの先頭に加える（10 件まで）</summary>
    public void AddRecent(string entryPath)
    {
        RecentProjects.RemoveAll(path => string.Equals(path, entryPath, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, entryPath);
        if (RecentProjects.Count > 10)
        {
            RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
        }
    }
}
