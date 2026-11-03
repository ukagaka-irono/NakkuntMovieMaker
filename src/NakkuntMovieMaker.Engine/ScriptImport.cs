using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Engine;

/// <summary>台本の 1 行（キャラクターとセリフ）</summary>
public sealed record ScriptLine(CharacterId Character, string Text);

/// <summary>
/// 台本の一括貼り付け。1 行を 1 つのセリフにして、レイヤーに順に並べる
/// </summary>
public static class ScriptImport
{
    // 声を合成するまでの仮の長さ：1 文字あたりの秒数と、最短の秒数
    private const double SecondsPerCharacter = 0.15;
    private const double MinimumSeconds = 1;

    /// <summary>
    /// 台本を読む。行の形は「名前：セリフ」「名前:セリフ」「名前「セリフ」」「名前『セリフ』」のどれか（名前はキャラクターの名前と同じもの）。
    /// 名前のない行は、直前の行のキャラクター（最初の行なら first）のセリフにする。空の行は飛ばす
    /// </summary>
    public static IReadOnlyList<ScriptLine> Parse(string script, IReadOnlyCollection<Character> characters, CharacterId first)
    {
        ArgumentNullException.ThrowIfNull(script);
        var lines = new List<ScriptLine>();
        var current = first;

        // 長い名前を先に試す（「霊夢」と「霊夢2」のような名前があっても取り違えない）
        var byName = characters.OrderByDescending(character => character.Name.Length).ToList();
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var text = line;
            foreach (var character in byName)
            {
                if (character.Name.Length > 0 && Split(line, character.Name) is { } spoken)
                {
                    current = character.Id;
                    text = spoken;
                    break;
                }
            }

            if (text.Length > 0)
            {
                lines.Add(new ScriptLine(current, text));
            }
        }

        return lines;
    }

    /// <summary>
    /// セリフを layer の startFrame から順に、gapFrames ずつあけて並べる操作。並べたアイテムの ID も返す。
    /// 声を合成するまでの長さは文字数から決め、字幕は画面の下のほうに置く
    /// </summary>
    /// <exception cref="EditException">ほかのアイテムと重なる、またはロックされたレイヤー</exception>
    public static (IOperation Operation, IReadOnlyList<ItemId> Items) CreateItems(
        Project project, SceneId sceneId, LayerId layer, long startFrame, IReadOnlyList<ScriptLine> lines, long gapFrames = 0)
    {
        var scene = project.GetScene(sceneId);
        var operations = new List<IOperation>();
        var ids = new List<ItemId>();
        var frame = startFrame;
        foreach (var line in lines)
        {
            var seconds = Math.Max(MinimumSeconds, new System.Globalization.StringInfo(line.Text).LengthInTextElements * SecondsPerCharacter);
            var length = Math.Max(1, (long)Math.Ceiling(seconds * scene.Fps.ToDouble()));
            var item = new VoiceItem(ItemId.New(), layer, frame, length, line.Character, line.Text)
            {
                Transform = Transform2D.Identity with { Y = scene.Format.Height * 0.38 },
            };
            var operation = EditCommands.AddItem(project, sceneId, item);
            operations.Add(operation);
            project = operation.Apply(project).Project;
            ids.Add(item.Id);
            frame += length + gapFrames;
        }

        return (Operations.Combine("台本の貼り付け", operations), ids);
    }

    // line が「name：」「name:」「name「…」」「name『…』」で始まっていれば、セリフの部分を返す
    private static string? Split(string line, string name)
    {
        if (!line.StartsWith(name, StringComparison.Ordinal) || line.Length == name.Length)
        {
            return null;
        }

        var rest = line[name.Length..];
        return rest[0] switch
        {
            '：' or ':' => rest[1..].Trim(),
            '「' when rest.EndsWith('」') => rest[1..^1].Trim(),
            '『' when rest.EndsWith('』') => rest[1..^1].Trim(),
            _ => null,
        };
    }
}
