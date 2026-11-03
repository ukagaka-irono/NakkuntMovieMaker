using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Audio;

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
/// 自動保存の間隔、音の出力デバイス。OK で設定ファイルに書く
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShortcutRegistry _shortcuts;
    private readonly AppSettings _settings;

    public SettingsViewModel(ShortcutRegistry shortcuts, AppSettings settings)
    {
        _shortcuts = shortcuts;
        _settings = settings;
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
