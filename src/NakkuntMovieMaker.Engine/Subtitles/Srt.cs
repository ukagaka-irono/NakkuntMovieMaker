using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NakkuntMovieMaker.Engine.Subtitles;

/// <summary>字幕の 1 つ（表示する時間と本文。本文の改行は \n）</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// SRT（SubRip）の字幕ファイルの読み書き。読むときは、よくある書き方の違い
/// （BOM・CRLF と LF・ミリ秒の区切りの「,」と「.」・番号のない字幕・余分な空行）を受け付け、
/// 本文の書式のタグ（&lt;i&gt; や {\an8} など）は取り除く
/// </summary>
public static partial class Srt
{
    /// <summary>SRT の本文を読む。時刻が読めない字幕は飛ばす</summary>
    public static IReadOnlyList<SubtitleCue> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var cues = new List<SubtitleCue>();
        var lines = content.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimeLine().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var text = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                text.Add(StripTags(lines[i].TrimEnd()));
            }

            var start = Time(match.Groups["start"].Value);
            var end = Time(match.Groups["end"].Value);
            if (end > start)
            {
                cues.Add(new SubtitleCue(start, end, string.Join("\n", text).Trim()));
            }
        }

        return cues;
    }

    /// <summary>SRT の本文を作る（番号は 1 から振り直し、改行は CRLF）</summary>
    public static string Write(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var cue in cues)
        {
            builder.Append(index++).Append("\r\n");
            builder.Append(Format(cue.Start)).Append(" --> ").Append(Format(cue.End)).Append("\r\n");
            builder.Append(cue.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
        }

        return builder.ToString();
    }

    /// <summary>ファイルに書く（UTF-8、BOM なし）</summary>
    public static void WriteFile(string path, IEnumerable<SubtitleCue> cues) =>
        File.WriteAllText(path, Write(cues), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>ファイルを読む（UTF-8。BOM があれば BOM に従う）</summary>
    public static IReadOnlyList<SubtitleCue> ReadFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private static string Format(TimeSpan time)
    {
        var milliseconds = (long)Math.Round(Math.Max(0, time.TotalMilliseconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00},{milliseconds % 1000:000}");
    }

    private static TimeSpan Time(string text)
    {
        var parts = text.Replace(',', '.').Split(':');
        var seconds = decimal.Parse(parts[^1], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var hours = parts.Length >= 3 ? int.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromMilliseconds((double)Math.Round(((hours * 60m + minutes) * 60m + seconds) * 1000m));
    }

    private static string StripTags(string line) => Tags().Replace(line, "");

    // 「00:00:01,000 --> 00:00:02,500」（時は省略可、ミリ秒の区切りは「,」か「.」、桁数は 1〜3）。後ろに位置の指定が続いてもよい
    [GeneratedRegex(@"^\s*(?<start>(\d+:)?\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*(?<end>(\d+:)?\d{1,2}:\d{1,2}[,.]\d{1,3})")]
    private static partial Regex TimeLine();

    // <i>・</i>・<font color="…"> などの HTML 風のタグと、{\an8} などの ASS の指定
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>|\{\\[^}]*\}")]
    private static partial Regex Tags();
}
