using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>ショートカットの一覧の 1 行</summary>
public sealed partial class ShortcutEntry(ShortcutCommand command) : ObservableObject
{
    public ShortcutCommand Command { get; } = command;

    public string Category => Command.Category;

    public string Name => Command.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GestureText), nameof(IsChanged))]
    public partial KeyGesture? Gesture { get; set; }

    public string GestureText => Gesture?.ToString() ?? "（なし）";

    public bool IsChanged => !Equals(Gesture, Command.DefaultGesture);
}

/// <summary>
/// 設定。キーボードショートカットの変更（行を選んで「キーを割り当てる」を押し、割り当てるキーを押す。ほかのコマンドが使っていたキーは、そちらから外す）と、
/// 自動保存の間隔、音の出力デバイス、書き出しに使う外部の ffmpeg（x264・x265）。OK で設定ファイルに書く
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShortcutRegistry _shortcuts;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;

    public SettingsViewModel(ShortcutRegistry shortcuts, AppSettings settings, IDialogService dialogs)
    {
        _shortcuts = shortcuts;
        _settings = settings;
        _dialogs = dialogs;
        foreach (var command in shortcuts.Commands.OrderBy(command => ShortcutRegistry.Defaults.ToList().IndexOf(command)))
        {
            Shortcuts.Add(new ShortcutEntry(command) { Gesture = shortcuts.GestureOf(command.Id) });
        }

        AutosaveMinutes = settings.AutosaveMinutes;
        OutputDevices.Add(new InputDeviceChoice(null, "既定のデバイス"));
        try
        {
            foreach (var device in AudioDevices.List(capture: false))
            {
                OutputDevices.Add(new InputDeviceChoice(device.Name, device.Name));
            }
        }
        catch (Exception ex) when (ex is AudioDeviceException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        OutputDevice = OutputDevices.FirstOrDefault(choice => choice.Name == settings.OutputDevice) ?? OutputDevices[0];
        FfmpegPath = settings.ExternalFfmpegPath;
        FfmpegCheck = CheckFfmpegAsync();
    }

    public ObservableCollection<ShortcutEntry> Shortcuts { get; } = [];

    public ObservableCollection<InputDeviceChoice> OutputDevices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand), nameof(ClearCommand), nameof(ResetCommand))]
    public partial ShortcutEntry? Selected { get; set; }

    /// <summary>次に押したキーを割り当てる状態</summary>
    [ObservableProperty]
    public partial bool IsCapturing { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial decimal AutosaveMinutes { get; set; }

    [ObservableProperty]
    public partial InputDeviceChoice? OutputDevice { get; set; }

    /// <summary>書き出しに使う外部の ffmpeg（使わなければ null）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFfmpegCommand))]
    public partial string? FfmpegPath { get; private set; }

    /// <summary>外部の ffmpeg を確かめた結果（版と、使えるエンコーダー）</summary>
    [ObservableProperty]
    public partial string FfmpegStatus { get; private set; } = "";

    /// <summary>最後に始めた外部の ffmpeg の確認（テストで待つため）</summary>
    public Task FfmpegCheck { get; private set; }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        if (await _dialogs.PickOpenFileAsync("外部の ffmpeg の実行ファイル", [FileKinds.All]) is { } path)
        {
            FfmpegPath = path;
            FfmpegCheck = CheckFfmpegAsync();
            await FfmpegCheck;
        }
    }

    [RelayCommand(CanExecute = nameof(HasFfmpeg))]
    private void ClearFfmpeg()
    {
        FfmpegPath = null;
        FfmpegCheck = CheckFfmpegAsync();
    }

    private bool HasFfmpeg() => FfmpegPath is not null;

    // 版と、x264・x265 が入っているかを調べて表示する（実行に少し時間がかかるので、別のスレッドで）
    private async Task CheckFfmpegAsync()
    {
        if (FfmpegPath is not { } path)
        {
            FfmpegStatus = "x264・x265 で書き出したいときに、外部の ffmpeg（自分で入手したもの。ソフトには同梱していません）を指定します";
            return;
        }

        FfmpegStatus = "確かめています…";
        try
        {
            var info = await Task.Run(() => ExternalFfmpeg.Probe(path));
            if (FfmpegPath != path)
            {
                return;
            }

            FfmpegStatus = info.Encoders.Count == 0
                ? $"ffmpeg {info.Version}：x264・x265 が入っていないため、書き出しには使えません"
                : $"ffmpeg {info.Version}：{string.Join("・", info.Encoders.Select(name => name.Replace("lib", "", StringComparison.Ordinal)))} を使えます";
        }
        catch (MediaException ex)
        {
            if (FfmpegPath == path)
            {
                FfmpegStatus = ex.Message;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Capture()
    {
        IsCapturing = true;
        Status = $"「{Selected!.Name}」に割り当てるキーを押してください（Esc でやめる）";
    }

    /// <summary>キーが押された（画面から呼ぶ）。割り当てを待っていれば割り当てて true</summary>
    public bool OnKey(Key key, KeyModifiers modifiers)
    {
        if (!IsCapturing || Selected is not { } entry)
        {
            return false;
        }

        // 修飾キーだけでは割り当てない
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return true;
        }

        IsCapturing = false;
        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            Status = "";
            return true;
        }

        Assign(entry, new KeyGesture(key, modifiers));
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Clear() => Assign(Selected!, null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reset() => Assign(Selected!, Selected!.Command.DefaultGesture);

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var entry in Shortcuts)
        {
            entry.Gesture = entry.Command.DefaultGesture;
        }

        Status = "すべて最初の割り当てに戻しました";
    }

    /// <summary>OK：割り当てと設定を反映して、設定ファイルに書く</summary>
    public void Apply(string settingsPath)
    {
        foreach (var entry in Shortcuts)
        {
            if (!Equals(_shortcuts.GestureOf(entry.Command.Id), entry.Gesture))
            {
                _shortcuts.Assign(entry.Command.Id, entry.Gesture);
            }
        }

        _settings.Shortcuts = _shortcuts.Overrides();
        _settings.AutosaveMinutes = (int)AutosaveMinutes;
        _settings.OutputDevice = OutputDevice?.Name;
        _settings.ExternalFfmpegPath = FfmpegPath;
        _settings.Save(settingsPath);
    }

    private bool HasSelection() => Selected is not null;

    // 割り当てる。同じキーを使っていたコマンドからは外す
    private void Assign(ShortcutEntry entry, KeyGesture? gesture)
    {
        var taken = gesture is null ? null : Shortcuts.FirstOrDefault(other => other != entry && Equals(other.Gesture, gesture));
        if (taken is not null)
        {
            taken.Gesture = null;
        }

        entry.Gesture = gesture;
        Status = taken is null ? "" : $"{gesture} は「{taken.Name}」から外しました";
    }
}
