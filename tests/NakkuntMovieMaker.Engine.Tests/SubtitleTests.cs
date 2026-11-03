using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Subtitles;

namespace NakkuntMovieMaker.Engine.Tests;

public sealed class SubtitleTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
    private readonly Project _project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30, 3);

    public SubtitleTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void ParsesCommonVariations()
    {
        const string srt = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>ゆっくり</i>していってね\r\n\r\n\r\n"
            + "00:00:03.000 --> 00:00:04.000 X1:10 X2:20\n{\\an8}番号のない字幕\n2 行目\n\n"
            + "3\n00:00:05,000 --> 00:00:04,000\n終わりが前なので飛ばす\n\n"
            + "4\n1:02:03,4 --> 1:02:04,25\n時は 1 桁、ミリ秒は短い\n";

        var cues = Srt.Parse(srt);

        Assert.Equal(
            [
                new SubtitleCue(Ms(1000), Ms(2500), "ゆっくりしていってね"),
                new SubtitleCue(Ms(3000), Ms(4000), "番号のない字幕\n2 行目"),
                new SubtitleCue(new TimeSpan(0, 1, 2, 3, 400), new TimeSpan(0, 1, 2, 4, 250), "時は 1 桁、ミリ秒は短い"),
            ],
            cues);
        Assert.Empty(Srt.Parse(""));
    }

    [Fact]
    public void WritesAndReadsBack()
    {
        SubtitleCue[] cues = [new(Ms(0), Ms(1234), "最初"), new(new TimeSpan(1, 2, 3) + Ms(45), new TimeSpan(1, 2, 5), "二つ目\n2 行目")];
        var path = Path.Combine(_folder, "subtitles.srt");

        Srt.WriteFile(path, cues);

        var text = File.ReadAllText(path);
        Assert.StartsWith("1\r\n00:00:00,000 --> 00:00:01,234\r\n最初\r\n\r\n2\r\n01:02:03,045 --> 01:02:05,000\r\n二つ目\r\n2 行目\r\n", text);
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]); // BOM なし
        Assert.Equal(cues, Srt.ReadFile(path));
    }

    [Fact]
    public void ExportsVoicesInTimeOrder()
    {
        var layers = _project.Root.Layers;
        var character = CharacterId.New();
        var second = new VoiceItem(ItemId.New(), layers[0].Id, 60, 30, character, "二つ目");
        var first = new VoiceItem(ItemId.New(), layers[1].Id, 15, 30, character, "最初");
        var text = new TextItem(ItemId.New(), layers[2].Id, 0, 15, "タイトル");
        var empty = new VoiceItem(ItemId.New(), layers[0].Id, 100, 10, character, " ");
        var scene = _project.Root with { Items = _project.Root.Items.Add(second.Id, second).Add(first.Id, first).Add(text.Id, text).Add(empty.Id, empty) };

        Assert.Equal(
            [new SubtitleCue(Ms(500), Ms(1500), "最初"), new SubtitleCue(Ms(2000), Ms(3000), "二つ目")],
            SubtitleFiles.Export(scene));
        Assert.Equal("タイトル", SubtitleFiles.Export(scene, includeText: true)[0].Text);

        // 非表示のレイヤーは含めない
        var hidden = scene with { Layers = scene.Layers.SetItem(1, scene.Layers[1] with { Visible = false }) };
        Assert.Equal(["二つ目"], SubtitleFiles.Export(hidden).Select(cue => cue.Text));

        // 範囲 [30, 75) を書き出すときは、範囲の始まりを 0 秒にして、範囲の外は切る
        Assert.Equal(
            [new SubtitleCue(Ms(0), Ms(500), "最初"), new SubtitleCue(Ms(1000), Ms(1500), "二つ目")],
            SubtitleFiles.Export(scene, from: 30, to: 75));
    }

    [Fact]
    public void ImportsCuesAsTextItems()
    {
        SubtitleCue[] cues = [new(Ms(1000), Ms(2000), "一"), new(Ms(1900), Ms(3010), "重なる"), new(Ms(5000), Ms(5010), "短すぎる")];
        var style = TextStyle.Default with { FontSize = 48 };

        var (operation, ids) = SubtitleFiles.Import(_project, _project.RootScene, _project.Root.Layers[0].Id, cues, startFrame: 30, style: style);
        var scene = operation.Apply(_project).Project.Root;

        Assert.Equal(2, ids.Count); // 0.01 秒の字幕は、フレームに合わせると長さが 0 になるので入れない
        var first = (TextItem)scene.GetItem(ids[0]);
        var second = (TextItem)scene.GetItem(ids[1]);
        Assert.Equal((60L, 27L, "一"), (first.Start, first.Length, first.Text)); // 次の字幕（87 フレーム目）と重ならないよう縮める
        Assert.Equal((87L, 33L), (second.Start, second.Length));
        Assert.Equal(style, first.Style);
        Assert.Equal(1080 * 0.38, first.Transform.Y.Value, 6);
    }

    [Fact]
    public void ImportsCuesAsVoicesAndRoundTrips()
    {
        var character = CharacterId.New();
        var original = new[] { new SubtitleCue(Ms(0), Ms(1000), "霊夢です"), new SubtitleCue(Ms(1500), Ms(2500), "魔理沙だぜ") };

        var (operation, ids) = SubtitleFiles.Import(_project, _project.RootScene, _project.Root.Layers[1].Id, original, character: character);
        var scene = operation.Apply(_project).Project.Root;

        Assert.All(ids, id => Assert.Equal(character, Assert.IsType<VoiceItem>(scene.GetItem(id)).Character));
        Assert.Equal(original, SubtitleFiles.Export(scene));

        // ほかのアイテムと重なるときは読み込まない
        var occupied = operation.Apply(_project).Project;
        Assert.Throws<EditException>(() => SubtitleFiles.Import(occupied, occupied.RootScene, occupied.Root.Layers[1].Id, original));
    }

    [Fact]
    public void UsesFrameStartTimesAt2997Fps()
    {
        var scene = Scene.CreateNew("テスト", SceneFormat.Landscape1080p30 with { Fps = Rational.Fps29_97 }, 1);
        var voice = new VoiceItem(ItemId.New(), scene.Layers[0].Id, 30, 30, CharacterId.New(), "あ");

        var cue = Assert.Single(SubtitleFiles.Export(scene with { Items = scene.Items.Add(voice.Id, voice) }));

        // 30 フレーム目は 1.001 秒、60 フレーム目は 2.002 秒
        Assert.Equal((Ms(1001), Ms(2002)), (cue.Start, cue.End));
        Assert.Contains("00:00:01,001 --> 00:00:02,002", Srt.Write([cue]));
    }
}
