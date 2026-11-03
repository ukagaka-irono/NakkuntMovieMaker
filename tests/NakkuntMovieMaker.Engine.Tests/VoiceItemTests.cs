using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.Engine.Tests;

public sealed class VoiceItemTests : IDisposable
{
    private static readonly TextStyle ReimuStyle = TextStyle.Default with { Color = new RgbaColor(255, 80, 80) };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
    private readonly Character _reimu = new(CharacterId.New(), "霊夢", new VoiceSpec("tone", "a", VoiceParams.Default), ReimuStyle);
    private readonly Character _marisa = new(CharacterId.New(), "魔理沙", new VoiceSpec("tone", "b", VoiceParams.Default), TextStyle.Default);

    // 30fps・レイヤー 2 つの元のプロジェクト（どのテストでもレイヤーの ID を同じにする）
    private readonly Project _base = Project.CreateNew("テスト", SceneFormat.Landscape1080p30, 2);

    public VoiceItemTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // 元のプロジェクトに、アイテムとキャラクター 2 人を加えたもの
    private Project CreateProject(params Item[] items)
    {
        var project = _base;
        var scene = project.Root with { Items = project.Root.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };
        return project.WithScene(scene) with
        {
            Characters = project.Characters.Add(_reimu.Id, _reimu).Add(_marisa.Id, _marisa),
        };
    }

    private static LayerId Layer(Project project, int index) => project.Root.Layers[index].Id;

    private VoiceItem Voice(Project project, long start, long length, string text = "こんにちは", CharacterId? character = null) =>
        new(ItemId.New(), Layer(project, 0), start, length, character ?? _reimu.Id, text);

    [Fact]
    public void DrawsSubtitlesWithTheCharacterStyle()
    {
        var empty = CreateProject();
        var voice = Voice(empty, 0, 30);
        var overridden = new VoiceItem(ItemId.New(), Layer(empty, 1), 0, 30, _reimu.Id, "上書き") { SubtitleOverride = TextStyle.Default };
        var silent = Voice(empty, 40, 30, text: "");
        var project = CreateProject(voice, overridden, silent);

        var nodes = TimelineEvaluator.Evaluate(project.Root, 0, timeline: new TimelineContext(project.Characters)).Nodes.Cast<DrawNode>().ToList();
        Assert.Equal(new TextContent("こんにちは", ReimuStyle), nodes[0].Content);
        Assert.Equal(new TextContent("上書き", TextStyle.Default), nodes[1].Content);

        // キャラクターを渡さなければ既定のスタイル、本文が空なら字幕を出さない
        Assert.Equal(new TextContent("こんにちは", TextStyle.Default), ((DrawNode)TimelineEvaluator.Evaluate(project.Root, 0).Nodes[0]).Content);
        Assert.Empty(TimelineEvaluator.Evaluate(project.Root, 50, timeline: new TimelineContext(project.Characters)).Nodes);
    }

    [Fact]
    public void PlaysSynthesizedVoicesFromTheCache()
    {
        var empty = CreateProject();
        var ready = Voice(empty, 0, 30) with { Source = new SynthesizedVoice("abc") };
        var pending = Voice(empty, 40, 30);
        var project = CreateProject(ready, pending);

        var plan = AudioEvaluator.Build(project.Root);

        var clip = Assert.Single(plan.Clips); // まだ合成していないセリフは鳴らさない
        Assert.Equal("abc", clip.VoiceKey);
        var samples = AudioMixdown.Render(plan, new VoiceOnlyAudio(), 0, 48000, Token);
        Assert.True(samples.Max(Math.Abs) > 0.1f, "声が鳴っていない");
    }

    [Fact]
    public void ApplyVoiceFitsTheLengthAndPushesLaterItems()
    {
        var empty = CreateProject();
        var before = Voice(empty, 0, 20, "前");
        var voice = Voice(empty, 20, 30);
        var after = Voice(empty, 60, 30, "後");
        var otherLayer = new ImageItem(ItemId.New(), Layer(empty, 1), 55, 30, AssetId.New());
        var project = CreateProject(before, voice, after, otherLayer);

        // 2 秒の声：長さは 60 フレームになり、[20, 80) が後ろのセリフ（60〜）と重なるので、後ろを 30 フレームずらす
        var edited = VoiceEditing.ApplyVoice(project, project.RootScene, voice.Id, new SynthesizedVoice("k"), TimeSpan.FromSeconds(2)).Apply(project).Project.Root;

        Assert.Equal((20L, 60L), (edited.GetItem(voice.Id).Start, edited.GetItem(voice.Id).Length));
        Assert.Equal(new SynthesizedVoice("k"), ((VoiceItem)edited.GetItem(voice.Id)).Source);
        Assert.Equal(90, edited.GetItem(after.Id).Start);
        Assert.Equal(0, edited.GetItem(before.Id).Start);
        Assert.Equal(55, edited.GetItem(otherLayer.Id).Start); // ほかのレイヤーは動かさない

        // 短くなったときは、後ろは動かさない
        var shorter = VoiceEditing.ApplyVoice(project, project.RootScene, voice.Id, new SynthesizedVoice("k"), TimeSpan.FromSeconds(0.5)).Apply(project).Project.Root;
        Assert.Equal(15, shorter.GetItem(voice.Id).Length);
        Assert.Equal(60, shorter.GetItem(after.Id).Start);
    }

    [Fact]
    public async Task SynthesizesVoicesAndSkipsUnchangedOnes()
    {
        var empty = CreateProject();
        var first = Voice(empty, 0, 30, "あいう");
        var second = Voice(empty, 40, 30, "えお", _marisa.Id) with { VoiceOverride = VoiceParams.Default with { Speed = 1.5 } };
        var recorded = Voice(empty, 80, 30) with { Source = new RecordedVoice(AssetId.New()) };
        var project = CreateProject(first, second, recorded);
        var engine = new FixedEngine();
        using var service = new VoiceService(new VoiceEngineRegistry([engine]), new VoiceCache(_folder));

        var (operation, failures) = await VoiceEditing.SynthesizeAsync(project, project.RootScene, [first.Id, second.Id, recorded.Id], service, Token);
        var edited = operation!.Apply(project).Project;

        Assert.Empty(failures);
        Assert.Equal(2, engine.Requests.Count); // 録音した声のセリフは合成しない
        Assert.Equal([1.0, 1.5], engine.Requests.Select(request => request.Params.Speed));
        var synthesized = (VoiceItem)edited.Root.GetItem(first.Id);
        Assert.NotNull(((SynthesizedVoice)synthesized.Source).CacheKey);
        Assert.Equal(VoiceEditing.LengthFor(TimeSpan.FromSeconds(0.3), 30), synthesized.Length);

        // もう一度合成しても、変わるセリフはない
        var (again, _) = await VoiceEditing.SynthesizeAsync(edited, edited.RootScene, [first.Id, second.Id], service, Token);
        Assert.Null(again);
        Assert.Equal(2, engine.Requests.Count);
    }

    [Fact]
    public async Task ReportsVoicesThatCannotBeSynthesized()
    {
        var empty = CreateProject();
        var orphan = Voice(empty, 0, 30, character: CharacterId.New());
        var noEngine = Voice(empty, 40, 30);
        var project = CreateProject(orphan, noEngine);
        using var service = new VoiceService(new VoiceEngineRegistry([]), new VoiceCache(_folder));

        var (operation, failures) = await VoiceEditing.SynthesizeAsync(project, project.RootScene, [orphan.Id, noEngine.Id], service, Token);

        Assert.Null(operation);
        Assert.Equal([orphan.Id, noEngine.Id], failures.Select(failure => failure.Item));
        Assert.Contains("キャラクター", failures[0].Message);
    }

    [Fact]
    public void UsesARecordingAsTheVoice()
    {
        var empty = CreateProject();
        var voice = Voice(empty, 10, 30);
        var project = CreateProject(voice);
        var recording = new RecordingResult(Path.Combine(_folder, "recordings", "line1.wav"), 72000, 48000, 1, 0, 0);

        var edited = VoiceEditing.UseRecording(project, project.RootScene, voice.Id, recording, _folder).Apply(project).Project;

        var item = (VoiceItem)edited.Root.GetItem(voice.Id);
        var source = Assert.IsType<RecordedVoice>(item.Source);
        Assert.Equal("recordings/line1.wav", edited.Assets[source.Asset].Path);
        Assert.Equal(45, item.Length); // 1.5 秒
    }

    [Fact]
    public void ParsesScripts()
    {
        const string script = """
            霊夢：ゆっくりしていってね
            魔理沙「今日は解説をするぜ」

            続きの行は、同じキャラクター
            霊夢:よろしくね
            注意：名前でなければ本文のまま
            """;

        var lines = ScriptImport.Parse(script, [_reimu, _marisa], _reimu.Id);

        Assert.Equal(
            [
                new ScriptLine(_reimu.Id, "ゆっくりしていってね"),
                new ScriptLine(_marisa.Id, "今日は解説をするぜ"),
                new ScriptLine(_marisa.Id, "続きの行は、同じキャラクター"),
                new ScriptLine(_reimu.Id, "よろしくね"),
                new ScriptLine(_reimu.Id, "注意：名前でなければ本文のまま"),
            ],
            lines);
    }

    [Fact]
    public void PlacesScriptLinesInOrder()
    {
        var project = CreateProject();
        ScriptLine[] lines = [new(_reimu.Id, "短い"), new(_marisa.Id, "これは少し長めのセリフです")];

        var (operation, ids) = ScriptImport.CreateItems(project, project.RootScene, Layer(project, 0), 30, lines, gapFrames: 6);
        var scene = operation.Apply(project).Project.Root;

        var first = (VoiceItem)scene.GetItem(ids[0]);
        var second = (VoiceItem)scene.GetItem(ids[1]);
        Assert.Equal((30L, 30L), (first.Start, first.Length)); // 最短 1 秒
        Assert.Equal((66L, 59L), (second.Start, second.Length)); // 13 文字 × 0.15 秒 = 1.95 秒
        Assert.Equal(1080 * 0.38, first.Transform.Y.Value, 6); // 字幕は画面の下のほう
        Assert.Equal(_marisa.Id, second.Character);

        // ほかのアイテムと重なるときは並べない
        var occupied = operation.Apply(project).Project;
        Assert.Throws<EditException>(() => ScriptImport.CreateItems(occupied, occupied.RootScene, Layer(occupied, 0), 40, lines));
    }

    // 声のキャッシュの代わりに、どのキーにも 440Hz の音を返す
    private sealed class VoiceOnlyAudio : IAudioSource
    {
        public IAudioStream? OpenAudio(AssetId asset) => null;

        public IAudioStream? OpenVoice(string key) =>
            new MemoryAudioStream([.. Enumerable.Range(0, 48000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000)))], 1, 48000);
    }

    // 1 文字を 0.1 秒の音にするエンジン
    private sealed class FixedEngine : IVoiceEngine
    {
        public List<VoiceRequest> Requests { get; } = [];

        public string Id => "tone";

        public string Name => "テスト用";

        public string Version => "1";

        public VoiceEngineStatus Status => VoiceEngineStatus.Ready;

        public IReadOnlyList<VoiceInfo> Voices { get; } = [new("a", "A"), new("b", "B")];

        public string? MakeReading(string text) => null;

        public VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return new VoiceAudio(new float[request.Text.Length * 2400], 24000);
        }

        public void Dispose()
        {
        }
    }
}
