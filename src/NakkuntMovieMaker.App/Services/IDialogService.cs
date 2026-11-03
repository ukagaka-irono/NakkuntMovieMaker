using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Services;

/// <summary>保存していない変更があるときの答え</summary>
public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>ファイルの種類（ファイルを選ぶ画面の絞り込み）</summary>
public sealed record FileKind(string Name, IReadOnlyList<string> Patterns);

/// <summary>
/// ダイアログとファイルを選ぶ画面。画面はすべてここを通して開く（テストでは差し替える）。
/// 失敗の知らせも ShowErrorAsync だけを使い、画面ごとにメッセージボックスを作らない
/// </summary>
public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);

    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string okText);

    Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string projectName);

    /// <summary>1 行の文字を入れてもらう。キャンセルなら null</summary>
    Task<string?> PromptAsync(string title, string label, string initial);

    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileKind> kinds);

    Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds);

    Task<string?> PickFolderAsync(string title);

    /// <summary>プロジェクトの設定（新規作成にも使う）。OK なら true</summary>
    Task<bool> ShowProjectSettingsAsync(ProjectSettingsViewModel settings);

    /// <summary>ダイアログを開く。view は Window（ShowDialog で開く）で、DataContext に viewModel を入れる。閉じたときの結果を返す</summary>
    Task<T?> ShowDialogAsync<T>(object view, object viewModel);
}

public static class FileKinds
{
    public static readonly FileKind Project = new("NakkuntMovieMaker プロジェクト", ["*.nmmproj"]);

    public static readonly FileKind Media = new(
        "動画・画像・音声",
        ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.m4v", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.wav", "*.mp3", "*.aac", "*.m4a", "*.flac", "*.ogg", "*.opus"]);

    public static readonly FileKind Subtitles = new("字幕（SRT）", ["*.srt"]);

    public static readonly FileKind Text = new("テキスト", ["*.txt"]);

    public static readonly FileKind Image = new("画像", ["*.png", "*.jpg", "*.jpeg", "*.webp"]);

    public static readonly FileKind Mp4 = new("MP4 動画", ["*.mp4"]);

    public static readonly FileKind Wav = new("WAV 音声", ["*.wav"]);

    public static readonly FileKind Lut = new("LUT（.cube）", ["*.cube"]);

    public static readonly FileKind All = new("すべてのファイル", ["*.*"]);
}
