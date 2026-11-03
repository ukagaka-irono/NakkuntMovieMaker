using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine.Tachie;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine.Tests;

public sealed class TachieTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public TachieTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // 立ち絵のフォルダに、空のファイルを置く（読み込みはファイルの名前だけを見る）
    private string Parts(params string[] files)
    {
        var root = Path.Combine(_folder, "立ち絵", "霊夢");
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        return root;
    }

    private string File_(string folder, string name) => Path.Combine(_folder, "立ち絵", "霊夢", folder, name);

    [Fact]
    public void LoadsPartsInDrawingOrder()
    {
        Parts("体/body.png", "口/a.0.png", "口/a.1.png", "口/a.png", "口/b.png", "目/e.0.png", "目/e.png", "後/bg.png", "他/memo.txt");
        var loader = new TachieLoader(_folder);

        var model = loader.Load(new TachieSpec(TachieKind.Parts, "立ち絵/霊夢"));

        Assert.NotNull(model);
        Assert.Equal(["後", "体", "口", "目"], model.Parts.Select(part => part.Name));
        Assert.Equal([File_("口", "a.0.png"), File_("口", "a.1.png"), File_("口", "a.png")], model.Parts[2].Frames);
        Assert.Equal(TachiePartKind.Mouth, model.Parts[2].Kind);
        Assert.Equal([File_("目", "e.0.png"), File_("目", "e.png")], model.Parts[3].Frames);
        Assert.Equal([File_("体", "body.png")], model.Parts[1].Frames);
        Assert.Same(model, loader.Load(new TachieSpec(TachieKind.Parts, "立ち絵/霊夢")));
    }

    [Fact]
    public void LoadsSimpleTachieAndReportsMissingOnes()
    {
        Parts("simple.png");
        var loader = new TachieLoader(_folder);

        var simple = loader.Load(new TachieSpec(TachieKind.Simple, "立ち絵/霊夢/simple.png"));

        Assert.Equal(TachiePartKind.Static, Assert.Single(simple!.Parts).Kind);
        Assert.Null(loader.Load(new TachieSpec(TachieKind.Simple, "missing.png")));
        Assert.Null(loader.Load(new TachieSpec(TachieKind.Parts, "missing")));
    }

    [Theory]
    [InlineData(5, 0f, 0)]
    [InlineData(5, 1f, 4)]
    [InlineData(5, 0.5f, 2)]
    [InlineData(3, 0.3f, 1)]
    [InlineData(1, 1f, 0)]
    public void MapsVoiceLevelsToMouthFrames(int frames, float level, int expected) =>
        Assert.Equal(expected, TachieAnimation.MouthFrame(frames, level));

    [Fact]
    public void BlinksAtSeededRandomIntervals()
    {
        // 4 枚の目（0 が閉じ、3 が開き）。30fps で 60 秒
        var seed = TachieAnimation.Seed(ItemId.New());
        var frames = Enumerable.Range(0, 1800).Select(local => TachieAnimation.EyeFrame(4, local, 30, seed)).ToArray();

        // 同じシードなら、何度でも同じ
        Assert.Equal(frames, Enumerable.Range(0, 1800).Select(local => TachieAnimation.EyeFrame(4, local, 30, seed)));
        Assert.NotEqual(frames, Enumerable.Range(0, 1800).Select(local => TachieAnimation.EyeFrame(4, local, 30, seed + 1)));

        // 瞬きは 2, 1, 0, 1, 2 と進み、間隔は 2.5〜5.5 秒（75〜165 フレーム）
        var closed = Enumerable.Range(0, frames.Length).Where(i => frames[i] == 0).ToList();
        Assert.InRange(closed.Count, 60 / 5.5, 60 / 2.5);
        Assert.All(closed.Zip(closed.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 75, 165 + 2));
        Assert.Equal([2, 1, 0, 1, 2, 3], frames[(closed[0] - 2)..(closed[0] + 4)]);
        Assert.Equal(3, frames[0]); // 始めは開いている
    }

    [Fact]
    public void BlinkStepsLastLongerAtHigherFrameRates()
    {
        var seed = 7;
        var at60 = Enumerable.Range(0, 3600).Select(local => TachieAnimation.EyeFrame(2, local, 60, seed)).ToArray();

        // 60fps では、閉じた目を 2 フレーム続ける
        var first = Array.IndexOf(at60, 0);
        Assert.Equal([1, 0, 0, 1], at60[(first - 1)..(first + 3)]);
    }

    [Fact]
    public void DrawsTachieWithLipSyncFromTheVoice()
    {
        Parts("体/body.png", "口/m.0.png", "口/m.1.png", "口/m.png", "目/e.0.png", "目/e.png");
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30, 3);
        var character = new Character(CharacterId.New(), "霊夢", new VoiceSpec("tone", "a", VoiceParams.Default), TextStyle.Default,
            new TachieSpec(TachieKind.Parts, "立ち絵/霊夢"));
        var tachie = new TachieItem(ItemId.New(), project.Root.Layers[0].Id, 0, 300, character.Id);
        var voice = new VoiceItem(ItemId.New(), project.Root.Layers[1].Id, 30, 30, character.Id, "あ") { Source = new SynthesizedVoice("k") };
        var other = new VoiceItem(ItemId.New(), project.Root.Layers[2].Id, 60, 30, CharacterId.New(), "別の人") { Source = new SynthesizedVoice("k") };
        var scene = project.Root with { Items = project.Root.Items.Add(tachie.Id, tachie).Add(voice.Id, voice).Add(other.Id, other) };
        var timeline = new TimelineContext(
            new Dictionary<CharacterId, Character> { [character.Id] = character },
            new TachieLoader(_folder),
            new FixedLipSync([0f, 0.5f, 1f]));

        string[] Layers(long frame, Scene target) =>
            [.. ((TachieContent)((DrawNode)TimelineEvaluator.Evaluate(target, frame, timeline: timeline).Nodes[0]).Content).Layers];

        // セリフの前は口を閉じ、セリフの中は声の大きさに合わせて開く。ほかのキャラクターのセリフでは動かない
        Assert.Equal([File_("体", "body.png"), File_("口", "m.0.png"), File_("目", "e.png")], Layers(10, scene));
        Assert.Equal(File_("口", "m.1.png"), Layers(31, scene)[1]);
        Assert.Equal(File_("口", "m.png"), Layers(32, scene)[1]);
        Assert.Equal(File_("口", "m.0.png"), Layers(61, scene)[1]);

        // セリフのレイヤーをミュートすると、口は動かない
        var muted = scene with { Layers = scene.Layers.SetItem(1, scene.Layers[1] with { Muted = true }) };
        Assert.Equal(File_("口", "m.0.png"), Layers(32, muted)[1]);

        // 立ち絵のないキャラクターは描かない
        var plain = new TimelineContext(new Dictionary<CharacterId, Character> { [character.Id] = character with { Tachie = null } }, new TachieLoader(_folder));
        Assert.DoesNotContain(TimelineEvaluator.Evaluate(scene, 10, timeline: plain).Nodes, node => node is DrawNode { Content: TachieContent });
    }

    [Fact]
    public void ComputesLipSyncFromTheVoiceCache()
    {
        // 0.5 秒の無音のあとに 0.5 秒の声
        var samples = new float[48000];
        for (var i = 24000; i < 48000; i++)
        {
            samples[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 200 * i / 48000));
        }

        var lipSync = new ProjectLipSync(new VoiceAudio(samples));
        var voice = new VoiceItem(ItemId.New(), LayerId.New(), 0, 30, CharacterId.New(), "あ") { Source = new SynthesizedVoice("k") };

        var envelope = lipSync.Envelope(voice, 30);

        Assert.NotNull(envelope);
        Assert.Equal(30, envelope.Length);
        Assert.Equal(0f, envelope[5]);
        Assert.True(envelope[20] > 0.9f);
        Assert.Same(envelope, lipSync.Envelope(voice, 30));
        Assert.Null(lipSync.Envelope(voice with { Source = new SynthesizedVoice() }, 30)); // まだ合成していない
    }

    private sealed class FixedLipSync(float[] envelope) : ILipSyncSource
    {
        public float[]? Envelope(VoiceItem voice, Rational fps) => envelope;
    }

    private sealed class VoiceAudio(float[] samples) : IAudioSource
    {
        public IAudioStream? OpenAudio(AssetId asset) => null;

        public IAudioStream? OpenVoice(string key) => new MemoryAudioStream(samples, 1, 48000);
    }
}
