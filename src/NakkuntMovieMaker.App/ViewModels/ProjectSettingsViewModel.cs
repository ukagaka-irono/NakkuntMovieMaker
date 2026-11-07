using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>形式のプリセット（横長・縦長・正方形）</summary>
public sealed record FormatPreset(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>フレームレートの候補</summary>
public sealed record FrameRateChoice(string Name, Rational Fps)
{
    public override string ToString() => Name;
}

/// <summary>合成の色空間の候補</summary>
public sealed record CompositingChoice(CompositingSpace Space, string Name)
{
    public override string ToString() => Name;
}

/// <summary>プロキシを使う範囲の候補</summary>
public sealed record ProxyModeChoice(ProxyMode Mode, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// プロジェクトの設定（新規作成と、あとからの変更の両方に使う）。形式と、色（合成の色空間・OpenColorIO の構成・書き出しの色空間）と、
/// プロキシを使う範囲
/// </summary>
public sealed partial class ProjectSettingsViewModel : ObservableObject
{
    public ProjectSettingsViewModel(
        string title, string name, SceneFormat format, bool isNew, ProjectColorSettings? color = null, ProjectProxySettings? proxies = null)
    {
        Title = title;
        IsNew = isNew;
        Name = name;
        Width = format.Width;
        Height = format.Height;
        FrameRate = FrameRates.FirstOrDefault(choice => choice.Fps == format.Fps) ?? new FrameRateChoice(format.Fps.ToString(), format.Fps);
        if (!FrameRates.Contains(FrameRate))
        {
            FrameRates.Add(FrameRate);
        }

        SampleRate = format.SampleRate;

        color ??= ProjectColorSettings.Default;
        Compositing = CompositingChoices.First(choice => choice.Space == color.Compositing);
        OcioConfigs = [.. ColorChoices.Configs()];
        OcioConfig = ColorChoices.Select(OcioConfigs, color.OcioConfig);
        ExportColorSpace = ColorChoices.Select(ExportColorSpaces, color.ExportColorSpace);
        ProxyMode = ProxyModes.First(choice => choice.Mode == (proxies ?? ProjectProxySettings.Default).Mode);
    }

    public string Title { get; }

    /// <summary>新規作成のとき true（フレームレートを変えたときの注意を出さない）</summary>
    public bool IsNew { get; }

    public static IReadOnlyList<FormatPreset> Presets { get; } =
    [
        new("横長 1920×1080（フル HD）", 1920, 1080),
        new("横長 1280×720（HD）", 1280, 720),
        new("横長 3840×2160（4K）", 3840, 2160),
        new("縦長 1080×1920（ショート動画）", 1080, 1920),
        new("正方形 1080×1080", 1080, 1080),
    ];

    public ObservableCollection<FrameRateChoice> FrameRates { get; } =
    [
        new("24", 24),
        new("23.976", Rational.Fps23_976),
        new("25", 25),
        new("29.97", Rational.Fps29_97),
        new("30", 30),
        new("50", 50),
        new("59.94", Rational.Fps59_94),
        new("60", 60),
    ];

    public static IReadOnlyList<int> SampleRates { get; } = [44100, 48000];

    public static IReadOnlyList<CompositingChoice> CompositingChoices { get; } =
    [
        new(CompositingSpace.Srgb, "sRGB（今までと同じ見た目）"),
        new(CompositingSpace.Linear, "リニア（光の量として混ぜる）"),
    ];

    /// <summary>OpenColorIO の組み込みの構成（Name が null なら既定）</summary>
    public ObservableCollection<ColorSpaceChoice> OcioConfigs { get; }

    /// <summary>書き出しの色空間（選んでいる構成のディスプレイの色空間。Name が null なら変換しない）</summary>
    public ObservableCollection<ColorSpaceChoice> ExportColorSpaces { get; } = [];

    /// <summary>OpenColorIO を使えない（構成と書き出しの色空間を選べない）</summary>
    public bool IsOcioUnavailable { get; } = !ColorChoices.IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinear))]
    public partial CompositingChoice Compositing { get; set; }

    /// <summary>リニアで合成する（見た目が変わることの注意を出す）</summary>
    public bool IsLinear => Compositing.Space == CompositingSpace.Linear;

    [ObservableProperty]
    public partial ColorSpaceChoice OcioConfig { get; set; }

    [ObservableProperty]
    public partial ColorSpaceChoice ExportColorSpace { get; set; }

    public ProjectColorSettings Color => new(Compositing.Space, OcioConfig.Name, ExportColorSpace.Name);

    public static IReadOnlyList<ProxyModeChoice> ProxyModes { get; } =
    [
        new(Core.Model.ProxyMode.Off, "使わない"),
        new(Core.Model.ProxyMode.LargeVideos, "1080p より大きい動画だけ"),
        new(Core.Model.ProxyMode.AllVideos, "すべての動画"),
    ];

    [ObservableProperty]
    public partial ProxyModeChoice ProxyMode { get; set; }

    public ProjectProxySettings Proxies => new(ProxyMode.Mode);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(IsValid))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(IsValid))]
    public partial int Width { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(IsValid))]
    public partial int Height { get; set; }

    [ObservableProperty]
    public partial FrameRateChoice FrameRate { get; set; }

    [ObservableProperty]
    public partial int SampleRate { get; set; }

    /// <summary>選んだプリセット（選ぶと幅と高さを入れる）</summary>
    [ObservableProperty]
    public partial FormatPreset? Preset { get; set; }

    /// <summary>入力の誤り。なければ null</summary>
    public string? Error =>
        string.IsNullOrWhiteSpace(Name) ? "名前を入れてください"
        : Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "名前に使えない文字があります"
        : Width is < 16 or > 8192 || Height is < 16 or > 8192 ? "幅と高さは 16〜8192 にしてください"
        : Width % 2 != 0 || Height % 2 != 0 ? "幅と高さは偶数にしてください（動画の書き出しのため）"
        : null;

    public bool IsValid => Error is null;

    public SceneFormat Format => new(Width, Height, FrameRate.Fps, SampleRate);

    // 構成を変えたら、書き出しの色空間の候補をその構成のものにする（選んでいた名前があれば、そのまま選ぶ）
    partial void OnOcioConfigChanged(ColorSpaceChoice value)
    {
        var selected = ExportColorSpace?.Name;
        ExportColorSpaces.Clear();
        foreach (var choice in ColorChoices.Displays(value.Name, "変換しない（BT.709）"))
        {
            ExportColorSpaces.Add(choice);
        }

        ExportColorSpace = ColorChoices.Select(ExportColorSpaces, selected);
    }

    partial void OnPresetChanged(FormatPreset? value)
    {
        if (value is not null)
        {
            Width = value.Width;
            Height = value.Height;
        }
    }
}
