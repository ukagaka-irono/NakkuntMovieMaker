using NakkuntMovieMaker.ColorManagement;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>色空間・OpenColorIO の構成の候補（Name は設定に書く名前。null なら「変換しない」「既定」「自動」）</summary>
public sealed record ColorSpaceChoice(string? Name, string Label)
{
    public override string ToString() => Label;
}

/// <summary>色の画面の候補を、OpenColorIO の組み込みの構成から作る。OpenColorIO を読み込めなければ、null の候補だけになる</summary>
public static class ColorChoices
{
    /// <summary>OpenColorIO を使えるか</summary>
    public static bool IsAvailable => Ocio.TryLoad(out _);

    /// <summary>組み込みの構成（先頭は「既定」）</summary>
    public static List<ColorSpaceChoice> Configs()
    {
        if (!IsAvailable)
        {
            return [new(null, "既定")];
        }

        var builtin = Ocio.BuiltinConfigs;
        var defaultName = Ocio.DefaultBuiltinConfig;
        var defaultLabel = builtin.FirstOrDefault(config => config.Name == defaultName)?.UiName ?? defaultName;
        return [new(null, $"既定（{defaultLabel}）"), .. builtin.Select(config => new ColorSpaceChoice(config.Name, config.UiName))];
    }

    /// <summary>ディスプレイの色空間（書き出し・プレビューの表示の変換先）。先頭は noneLabel（変換しない）</summary>
    public static List<ColorSpaceChoice> Displays(string? configName, string noneLabel) =>
        [new(null, noneLabel), .. Names(configName, config => config.Displays)];

    /// <summary>構成のすべての色空間（素材の入力の色空間）。先頭は noneLabel（自動）</summary>
    public static List<ColorSpaceChoice> ColorSpaces(string? configName, string noneLabel) =>
        [new(null, noneLabel), .. Names(configName, config => config.ColorSpaces)];

    /// <summary>
    /// 候補の中から name を選ぶ。候補にない名前（ほかの構成で選んだものなど）は、「（見つかりません）」を付けて候補に加えて選ぶ
    /// （設定を勝手に変えないため）
    /// </summary>
    public static ColorSpaceChoice Select(IList<ColorSpaceChoice> choices, string? name)
    {
        if (choices.FirstOrDefault(choice => choice.Name == name) is { } found)
        {
            return found;
        }

        var missing = new ColorSpaceChoice(name, $"{name}（見つかりません）");
        choices.Add(missing);
        return missing;
    }

    private static IEnumerable<ColorSpaceChoice> Names(string? configName, Func<OcioConfig, IEnumerable<string>> names)
    {
        if (!IsAvailable)
        {
            return [];
        }

        try
        {
            return [.. names(ColorTransforms.Shared.Config(configName)).Select(name => new ColorSpaceChoice(name, name))];
        }
        catch (OcioException)
        {
            return [];
        }
    }
}
