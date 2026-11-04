using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Services;

/// <summary>テキストのプリセット（名前の付いたスタイル）。BuiltIn は組み込みのもの（消せない）</summary>
public sealed record TextPreset(string Name, TextStyle Style, bool BuiltIn = false)
{
    public override string ToString() => Name;
}

/// <summary>設定ファイルに保存する、利用者が作ったテキストのプリセット</summary>
public sealed class SavedTextPreset
{
    public string Name { get; set; } = "";

    public TextStyle Style { get; set; } = TextStyle.Default;
}

/// <summary>
/// テキストのプリセットの一覧。組み込みのものと、利用者が保存したもの（アプリの設定に保存し、どのプロジェクトでも使える）
/// </summary>
public sealed class TextPresets(AppSettings settings)
{
    /// <summary>一覧が変わった</summary>
    public event EventHandler? Changed;

    public static IReadOnlyList<TextPreset> BuiltIn { get; } =
    [
        new("字幕（白・黒の縁取り）", TextStyle.Default with { FontSize = 64 }, true),
        new("タイトル（大きく・太字）", TextStyle.Default with
        {
            FontSize = 120,
            Bold = true,
            Outline = new TextOutline(new RgbaColor(24, 40, 82), 8),
            Shadow = new TextShadow(new RgbaColor(0, 0, 0, 160), 6, 6, 8),
        }, true),
        new("テロップ（黄色）", TextStyle.Default with { FontSize = 80, Bold = true, Color = new RgbaColor(255, 220, 60), Outline = new TextOutline(RgbaColor.Black, 6) }, true),
        new("見出し（背景つき）", TextStyle.Default with { FontSize = 72, Outline = null, Background = new TextBackground(new RgbaColor(0, 0, 0, 180), 16, 12) }, true),
        new("縦書き", TextStyle.Default with { FontSize = 72, Vertical = true }, true),
    ];

    public IReadOnlyList<TextPreset> All =>
        [.. BuiltIn, .. settings.TextPresets.Select(preset => new TextPreset(preset.Name, preset.Style))];

    /// <summary>スタイルを名前を付けて保存する（同じ名前の保存したプリセットがあれば置き換える）</summary>
    /// <exception cref="ArgumentException">名前が空、または組み込みのプリセットと同じ名前</exception>
    public void Save(string name, TextStyle style)
    {
        name = name.Trim();
        if (name.Length == 0 || BuiltIn.Any(preset => preset.Name == name))
        {
            throw new ArgumentException(name.Length == 0 ? "名前を入れてください" : "組み込みのプリセットと同じ名前は使えません", nameof(name));
        }

        settings.TextPresets.RemoveAll(preset => preset.Name == name);
        settings.TextPresets.Add(new SavedTextPreset { Name = name, Style = style });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存したプリセットを消す（組み込みのものは消せない）</summary>
    public void Remove(string name)
    {
        if (settings.TextPresets.RemoveAll(preset => preset.Name == name) > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
