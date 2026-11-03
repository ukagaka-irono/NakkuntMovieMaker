using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Voice.AquesTalk;

namespace NakkuntMovieMaker.Voice.Tests;

/// <summary>
/// AquesTalk。ライブラリは同梱しないので、ふだんは「導入されていない」ときの振る舞いを確かめる。
/// 環境変数 NMM_AQUESTALK_DIR に導入したフォルダ（engines/aquestalk にあたるもの）を指定すると、実際に合成するテストも動く
/// </summary>
public sealed class AquesTalkEngineTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    public AquesTalkEngineTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AquesTalkOptions Empty => new(_folder);

    private static AquesTalkOptions? Installed =>
        Environment.GetEnvironmentVariable("NMM_AQUESTALK_DIR") is { Length: > 0 } folder ? new AquesTalkOptions(folder) : null;

    [Fact]
    public void EnginesAreUnavailableUntilInstalled()
    {
        using var one = new AquesTalk1Engine(Empty);
        using var ten = new AquesTalk10Engine(Empty);
        using var two = new AquesTalk2Engine(Empty);

        foreach (IVoiceEngine engine in new IVoiceEngine[] { one, ten, two })
        {
            Assert.False(engine.Status.Available);
            Assert.Contains("導入されていません", engine.Status.Message);
            Assert.Contains("aquestalk", engine.Status.Message);
            Assert.StartsWith("https://www.a-quest.com/", engine.Status.HelpUrl);
            Assert.Throws<VoiceEngineException>(() => engine.Synthesize(new VoiceRequest("ゆっくり", null, "f1", VoiceParams.Default), CancellationToken.None));
        }

        Assert.Empty(one.Voices);
        Assert.Null(AqKanji2KoeConverter.TryCreate(Empty));
    }

    [Fact]
    public void AquesTalk1ListsVoicesFromTheFolders()
    {
        // ライブラリのファイルがあるフォルダだけが声になる（中身が壊れていれば、合成のときに知らせる）
        var root = Empty.ProductFolder("aquestalk1");
        Directory.CreateDirectory(Path.Combine(root, "f1"));
        Directory.CreateDirectory(Path.Combine(root, "m1"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        var library = OperatingSystem.IsWindows() ? "AquesTalk.dll" : OperatingSystem.IsMacOS() ? "libAquesTalk.dylib" : "libAquesTalk.so";
        File.WriteAllText(Path.Combine(root, "f1", library), "壊れたライブラリ");
        File.WriteAllText(Path.Combine(root, "m1", library), "壊れたライブラリ");

        using var engine = new AquesTalk1Engine(Empty);

        Assert.Equal(["f1", "m1"], engine.Voices.Select(voice => voice.Id));
        Assert.True(engine.Status.Available);
        var error = Assert.Throws<VoiceEngineException>(() => engine.Synthesize(new VoiceRequest("ゆっくり", null, "f1", VoiceParams.Default), CancellationToken.None));
        Assert.Contains("読み込めません", error.Message);
    }

    [Fact]
    public void ReadingsComeFromTheConverterOrTheText()
    {
        using var plain = new AquesTalk10Engine(Empty);
        using var converted = new AquesTalk10Engine(Empty, new FakeConverter());

        Assert.Equal("ゆっくりしていってね", plain.MakeReading("ゆっくりしていってね"));
        Assert.Equal("<ヨミ>今日", converted.MakeReading("今日"));
        Assert.Equal(["F1E", "F2E", "M1E"], plain.Voices.Select(voice => voice.Id));
    }

    [Fact]
    public void AquesTalk2ListsPhontFiles()
    {
        var phont = Path.Combine(Empty.ProductFolder("aquestalk2"), "phont");
        Directory.CreateDirectory(phont);
        File.WriteAllBytes(Path.Combine(phont, "aq_yukkuri.phont"), [1, 2, 3]);

        using var engine = new AquesTalk2Engine(Empty);

        Assert.Equal([AquesTalk2Engine.DefaultVoice, "aq_yukkuri"], engine.Voices.Select(voice => voice.Id));
    }

    [Fact]
    public void SynthesizesWithInstalledAquesTalk1()
    {
        var options = Installed;
        Assert.SkipWhen(options is null, "AquesTalk を導入していないためスキップします（NMM_AQUESTALK_DIR で場所を指定すると動きます）");
        using var converter = AqKanji2KoeConverter.TryCreate(options);
        using var engine = new AquesTalk1Engine(options, converter);
        Assert.SkipWhen(engine.Voices.Count == 0, "AquesTalk1 の声がないためスキップします");

        var voice = engine.Voices[0].Id;
        var audio = engine.Synthesize(new VoiceRequest("ゆっくりしていってね", engine.MakeReading("ゆっくりしていってね"), voice, VoiceParams.Default), CancellationToken.None);

        Assert.True(audio.Duration > TimeSpan.FromSeconds(0.5));
        Assert.True(audio.Samples.Max(Math.Abs) > 0.01f);
    }

    [Fact]
    public void SynthesizesWithInstalledAquesTalk10()
    {
        var options = Installed;
        Assert.SkipWhen(options is null, "AquesTalk を導入していないためスキップします（NMM_AQUESTALK_DIR で場所を指定すると動きます）");
        using var engine = new AquesTalk10Engine(options);
        Assert.SkipUnless(engine.Status.Available, "AquesTalk10 がないためスキップします");

        var audio = engine.Synthesize(new VoiceRequest("こんにちわ", null, "F1E", VoiceParams.Default), CancellationToken.None);

        Assert.True(audio.Duration > TimeSpan.FromSeconds(0.3));
    }

    private sealed class FakeConverter : IPhoneticConverter
    {
        public string Convert(string text) => $"<ヨミ>{text}";
    }
}
