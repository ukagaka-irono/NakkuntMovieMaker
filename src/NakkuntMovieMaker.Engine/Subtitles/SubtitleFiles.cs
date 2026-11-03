using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Engine.Subtitles;

/// <summary>シーンの字幕を字幕ファイルにする・字幕ファイルからアイテムを作る</summary>
public static class SubtitleFiles
{
    /// <summary>
    /// シーンの字幕。セリフの本文（includeText なら、テキストのアイテムも）を、表示される時間とともに開始の順に並べる。
    /// 非表示のレイヤーと、本文が空のアイテムは含めない。範囲 [from, to)（フレーム）を指定すると、その範囲の字幕を、
    /// 範囲の始まりを 0 秒として返す（範囲を指定した書き出しに合わせる）
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Export(Scene scene, bool includeText = false, long? from = null, long? to = null)
    {
        var start = from ?? 0;
        var end = to ?? long.MaxValue;
        var visible = scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
        var fps = scene.Fps;
        var offset = TimeMath.FrameToTimeSpan(start, fps);

        return scene.Items.Values
            .Where(item => visible.Contains(item.Layer) && item.End > start && item.Start < end)
            .Select(item => (Item: item, Text: item switch
            {
                VoiceItem voice => voice.Text,
                TextItem text when includeText => text.Text,
                _ => null,
            }))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .OrderBy(entry => entry.Item.Start)
            .ThenBy(entry => scene.LayerIndex(entry.Item.Layer))
            .Select(entry => new SubtitleCue(
                TimeMath.FrameToTimeSpan(Math.Max(entry.Item.Start, start), fps) - offset,
                TimeMath.FrameToTimeSpan(Math.Min(entry.Item.End, end), fps) - offset,
                entry.Text!))
            .ToList();
    }

    /// <summary>
    /// 字幕から、layer にアイテムを並べる操作（並べたアイテムの ID も返す）。character を指定するとそのキャラクターのセリフ
    /// （声は後で合成する）、しなければ style のテキストのアイテムにする。startFrame は字幕の 0 秒を置くフレーム。
    /// 時刻は最も近いフレームに合わせ、前の字幕と重なるときは前の字幕を縮める。字幕は画面の下のほうに置く
    /// </summary>
    /// <exception cref="EditException">ほかのアイテムと重なる、またはロックされたレイヤー</exception>
    public static (IOperation Operation, IReadOnlyList<ItemId> Items) Import(
        Project project,
        SceneId sceneId,
        LayerId layer,
        IReadOnlyList<SubtitleCue> cues,
        long startFrame = 0,
        CharacterId? character = null,
        TextStyle? style = null)
    {
        var scene = project.GetScene(sceneId);
        var fps = scene.Fps;
        var spans = cues
            .OrderBy(cue => cue.Start)
            .Select(cue => (
                Start: startFrame + TimeMath.TimeSpanToFrame(cue.Start, fps, Rounding.Nearest),
                End: startFrame + TimeMath.TimeSpanToFrame(cue.End, fps, Rounding.Nearest),
                cue.Text))
            .ToList();

        var operations = new List<IOperation>();
        var ids = new List<ItemId>();
        var transform = Transform2D.Identity with { Y = scene.Format.Height * 0.38 };
        for (var i = 0; i < spans.Count; i++)
        {
            var (start, end, text) = spans[i];
            if (i + 1 < spans.Count)
            {
                end = Math.Min(end, spans[i + 1].Start);
            }

            if (end <= start || text.Length == 0)
            {
                continue;
            }

            Item item = character is { } id
                ? new VoiceItem(ItemId.New(), layer, start, end - start, id, text) { Transform = transform }
                : new TextItem(ItemId.New(), layer, start, end - start, text) { Style = style ?? TextStyle.Default, Transform = transform };
            var operation = EditCommands.AddItem(project, sceneId, item);
            operations.Add(operation);
            project = operation.Apply(project).Project;
            ids.Add(item.Id);
        }

        return (Operations.Combine("字幕の読み込み", operations), ids);
    }
}
