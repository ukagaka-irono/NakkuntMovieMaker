using System.Globalization;
using System.Text;

namespace NakkuntMovieMaker.Voice.ColorfulTalk;

/// <summary>
/// ColorfulTalk の「記号列」（カナとアクセント記号）を作る。ColorfulTalk は辞書を持たず漢字を読めないので、
/// ひらがな・カタカナと句読点だけの文はそのまま記号列にし、それ以外の文字があれば、読みを入れてもらうよう知らせる
/// </summary>
public static class KoeText
{
    // 記号列で使える記号（ColorfulTalk の docs/koe-format.md）
    private const string Symbols = "ー～〜'’＇/／、,，。.．！!？?_＿ 　\r\n\t";

    // 読まない記号（かっこや引用符など）
    private const string Ignored = "「」『』（）()〈〉《》【】［］[]〔〕\"“”‘♪☆★♡";

    /// <summary>
    /// 本文から記号列を作る。カナ・句読点以外（漢字・英数字など）があれば VoiceEngineException。
    /// アクセント記号はないので、句ごとに平板型で読む（直したいときは読みを入れる）
    /// </summary>
    /// <exception cref="VoiceEngineException">漢字・英数字などが含まれている</exception>
    public static string FromText(string text)
    {
        var builder = new StringBuilder(text.Length);
        var unreadable = new List<string>();
        foreach (var element in TextElements(text))
        {
            if (element is "…" or "‥")
            {
                builder.Append('、');
            }
            else if (element is "・")
            {
                builder.Append('/');
            }
            else if (Ignored.Contains(element, StringComparison.Ordinal) && element.Length == 1)
            {
                continue;
            }
            else if (IsKana(element) || (element.Length == 1 && Symbols.Contains(element[0])))
            {
                builder.Append(element);
            }
            else if (!unreadable.Contains(element))
            {
                unreadable.Add(element);
            }
        }

        if (unreadable.Count > 0)
        {
            var shown = string.Concat(unreadable.Take(8)) + (unreadable.Count > 8 ? "…" : "");
            throw new VoiceEngineException(
                $"ColorfulTalk は漢字・英数字を読めません（「{shown}」）。セリフの「読み」に、カナとアクセント記号で読みを入れてください（例: きょ'うわ/い'い/てんきです。）");
        }

        return builder.ToString();
    }

    private static bool IsKana(string element) => element.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    private static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }
}
