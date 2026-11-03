using System.Windows.Input;
using Avalonia.Input;

namespace NakkuntMovieMaker.App.Services;

/// <summary>ショートカットで呼べるコマンド。Id は設定ファイルに書く名前（変えないこと）</summary>
public sealed record ShortcutCommand(string Id, string Category, string Name, KeyGesture? DefaultGesture);

/// <summary>
/// キーボードショートカットの一覧。コマンドごとにキーを 1 つ割り当てる（利用者が変えられる）。
/// メニューに表示するキーもここから取る。修飾キーのないキー（Space・J・K・L など）は、文字の入力欄にいるときは使わない
/// </summary>
public sealed class ShortcutRegistry
{
    private readonly Dictionary<string, ShortcutCommand> _commands = [];
    private readonly Dictionary<string, KeyGesture?> _gestures = [];
    private readonly Dictionary<string, ICommand> _handlers = [];

    public ShortcutRegistry()
    {
        foreach (var command in Defaults)
        {
            _commands.Add(command.Id, command);
            _gestures.Add(command.Id, command.DefaultGesture);
        }
    }

    /// <summary>割り当てが変わった</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<ShortcutCommand> Commands => _commands.Values;

    /// <summary>組み込みのコマンドと、最初のキーの割り当て</summary>
    public static IReadOnlyList<ShortcutCommand> Defaults { get; } =
    [
        new("file.new", "ファイル", "新規作成", Gesture("Ctrl+N")),
        new("file.open", "ファイル", "開く", Gesture("Ctrl+O")),
        new("file.save", "ファイル", "保存", Gesture("Ctrl+S")),
        new("file.saveAs", "ファイル", "名前を付けて保存", Gesture("Ctrl+Shift+S")),
        new("file.export", "ファイル", "書き出し", Gesture("Ctrl+E")),
        new("file.projectSettings", "ファイル", "プロジェクトの設定", null),
        new("edit.undo", "編集", "元に戻す", Gesture("Ctrl+Z")),
        new("edit.redo", "編集", "やり直し", Gesture("Ctrl+Y")),
        new("edit.cut", "編集", "切り取り", Gesture("Ctrl+X")),
        new("edit.copy", "編集", "コピー", Gesture("Ctrl+C")),
        new("edit.paste", "編集", "貼り付け", Gesture("Ctrl+V")),
        new("edit.delete", "編集", "削除", Gesture("Delete")),
        new("edit.rippleDelete", "編集", "削除して詰める", Gesture("Shift+Delete")),
        new("edit.split", "編集", "分割", Gesture("S")),
        new("edit.duplicate", "編集", "複製", Gesture("Ctrl+D")),
        new("edit.group", "編集", "グループ化", Gesture("Ctrl+G")),
        new("edit.ungroup", "編集", "グループ解除", Gesture("Ctrl+Shift+G")),
        new("edit.selectAll", "編集", "すべて選択", Gesture("Ctrl+A")),
        new("play.toggle", "再生", "再生・停止", Gesture("Space")),
        new("play.backward", "再生", "逆方向に再生（J）", Gesture("J")),
        new("play.pause", "再生", "停止（K）", Gesture("K")),
        new("play.forward", "再生", "再生・速める（L）", Gesture("L")),
        new("play.previousFrame", "再生", "1 フレーム戻る", Gesture("Left")),
        new("play.nextFrame", "再生", "1 フレーム進む", Gesture("Right")),
        new("play.start", "再生", "先頭へ", Gesture("Home")),
        new("play.end", "再生", "末尾へ", Gesture("End")),
        new("timeline.marker", "タイムライン", "マーカーを置く", Gesture("M")),
        new("timeline.rangeIn", "タイムライン", "範囲の始まり", Gesture("I")),
        new("timeline.rangeOut", "タイムライン", "範囲の終わり", Gesture("O")),
        new("timeline.clearRange", "タイムライン", "範囲を解除", Gesture("Ctrl+Shift+X")),
        new("timeline.zoomIn", "タイムライン", "拡大", Gesture("Ctrl+OemPlus")),
        new("timeline.zoomOut", "タイムライン", "縮小", Gesture("Ctrl+OemMinus")),
        new("timeline.snap", "タイムライン", "スナップの切り替え", Gesture("N")),
        new("view.fullScreenPreview", "表示", "全画面プレビュー", Gesture("F11")),
        new("dialogue.focus", "セリフ", "セリフ入力欄へ", Gesture("Ctrl+Enter")),
        new("record.toggle", "録音", "録音の開始・停止", Gesture("Ctrl+R")),
    ];

    public ShortcutCommand Get(string id) => _commands[id];

    public KeyGesture? GestureOf(string id) => _gestures.GetValueOrDefault(id);

    /// <summary>コマンドを実行する処理をつなぐ</summary>
    public void Bind(string id, ICommand command)
    {
        if (!_commands.ContainsKey(id))
        {
            throw new ArgumentException($"知らないコマンドです: {id}", nameof(id));
        }

        _handlers[id] = command;
    }

    /// <summary>キーを割り当てる（null なら割り当てなし）。同じキーを使っていたコマンドからは外し、その ID を返す</summary>
    public string? Assign(string id, KeyGesture? gesture)
    {
        string? previous = null;
        if (gesture is not null)
        {
            previous = _gestures.FirstOrDefault(pair => pair.Key != id && pair.Value is { } other && other.Equals(gesture)).Key;
            if (previous is not null)
            {
                _gestures[previous] = null;
            }
        }

        _gestures[id] = gesture;
        Changed?.Invoke(this, EventArgs.Empty);
        return previous;
    }

    /// <summary>すべて最初の割り当てに戻す</summary>
    public void ResetAll()
    {
        foreach (var command in _commands.Values)
        {
            _gestures[command.Id] = command.DefaultGesture;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>設定ファイルの変更分を読み込む（読めないキーは無視する）</summary>
    public void Load(IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (id, text) in overrides)
        {
            if (!_commands.ContainsKey(id))
            {
                continue;
            }

            if (text.Length == 0)
            {
                _gestures[id] = null;
            }
            else if (TryParse(text, out var gesture))
            {
                _gestures[id] = gesture;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>最初の割り当てと違うものだけを、設定ファイルに書く形で返す</summary>
    public Dictionary<string, string> Overrides() =>
        _commands.Values
            .Where(command => !Equals(command.DefaultGesture, _gestures[command.Id]))
            .ToDictionary(command => command.Id, command => _gestures[command.Id]?.ToString() ?? "");

    /// <summary>
    /// 押されたキーに割り当てたコマンドを実行する。文字の入力中（textInputFocused）は、
    /// 修飾キー（Ctrl・Alt）のないキーと、入力欄が使う Ctrl のキー（コピー・貼り付けなど）を入力欄に任せる
    /// </summary>
    public bool TryHandle(Key key, KeyModifiers modifiers, bool textInputFocused)
    {
        var pressed = new KeyGesture(key, modifiers);
        var id = _gestures.FirstOrDefault(pair => pair.Value is { } gesture && gesture.Equals(pressed)).Key;
        if (id is null || !_handlers.TryGetValue(id, out var command))
        {
            return false;
        }

        if (textInputFocused && (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Alt) || IsTextEditingKey(id)))
        {
            return false;
        }

        if (!command.CanExecute(null))
        {
            return true;
        }

        command.Execute(null);
        return true;
    }

    public static bool TryParse(string text, out KeyGesture? gesture)
    {
        try
        {
            gesture = KeyGesture.Parse(text);
            return true;
        }
        catch (ArgumentException)
        {
            gesture = null;
            return false;
        }
    }

    private static bool IsTextEditingKey(string id) =>
        id is "edit.undo" or "edit.redo" or "edit.cut" or "edit.copy" or "edit.paste" or "edit.selectAll" or "edit.delete";

    private static KeyGesture Gesture(string text) => KeyGesture.Parse(text);
}
