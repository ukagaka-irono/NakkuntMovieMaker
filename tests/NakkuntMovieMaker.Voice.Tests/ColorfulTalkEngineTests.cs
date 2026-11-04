using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Voice.ColorfulTalk;

namespace NakkuntMovieMaker.Voice.Tests;

/// <summary>
/// ColorfulTalk（旧版）。ライブラリは tools/build-native で作る。ない環境ではスキップする（NMM_REQUIRE_COLORFULTALK=1 なら失敗にする）
/// </summary>
public sealed class ColorfulTalkEngineTests : IDisposable
{
    private readonly ColorfulTalkEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    private void RequireLibrary()
    {
        var available = _engine.Status.Available;
        Assert.SkipWhen(!available && Environment.GetEnvironmentVariable("NMM_REQUIRE_COLORFULTALK") != "1", $"ColorfulTalk を読み込めないためスキップします（{_engine.Status.Message}）");
        Assert.True(available, _engine.Status.Message);
    }

    private VoiceAudio Synthesize(string? reading, string voice = "f1", VoiceParams? parameters = null, string text = "") =>
        _engine.Synthesize(new VoiceRequest(text, reading, voice, parameters ?? VoiceParams.Default), TestContext.Current.CancellationToken);

    [Fact]
    public void SynthesizesKoeStrings()
    {
        RequireLibrary();

        var audio = Synthesize("こんにちわ、せか'い。");

        Assert.Equal(ColorfulTalkEngine.SampleRate, audio.SampleRate);
        Assert.InRange(audio.Duration.TotalSeconds, 0.6, 3);
        Assert.True(audio.Samples.Max(Math.Abs) > 0.05f, "音が出ている");
        Assert.Equal("1.0.0", _engine.Version);
        Assert.Equal(["f1", "m1", "child", "robot"], _engine.Voices.Select(voice => voice.Id));
    }

    [Fact]
    public void ChangesVoiceAndSpeed()
    {
        RequireLibrary();

        var normal = Synthesize("きょ'うわ/い'い/てんきです。");
        var fast = Synthesize("きょ'うわ/い'い/てんきです。", parameters: VoiceParams.Default with { Speed = 2 });
        var male = Synthesize("きょ'うわ/い'い/てんきです。", "m1");

        Assert.True(fast.Duration < normal.Duration * 0.7, $"速くすると短くなる（{normal.Duration} → {fast.Duration}）");
        Assert.NotEqual(normal.Samples, male.Samples);
    }

    [Fact]
    public void ReadsKanaTextWithoutAReading()
    {
        RequireLibrary();

        Assert.Equal("ゆっくりしていってね！", _engine.MakeReading("「ゆっくりしていってね！」"));
        Assert.True(Synthesize(null, text: "ゆっくりしていってね").Duration.TotalSeconds > 0.5);
    }

    [Fact]
    public void ExplainsWhatIsWrongWithTheReading()
    {
        RequireLibrary();

        var accent = Assert.Throws<VoiceEngineException>(() => Synthesize("'あい"));
        Assert.Contains("1 文字目", accent.Message);
        Assert.Contains("アクセント", accent.Message);

        var kanji = Assert.Throws<VoiceEngineException>(() => Synthesize("あ漢"));
        Assert.Contains("2 文字目（「漢」）", kanji.Message);

        Assert.Contains("発音する文字", Assert.Throws<VoiceEngineException>(() => Synthesize("、。")).Message);
    }

    [Theory]
    [InlineData("こんにちわ、せかい。", "こんにちわ、せかい。")]
    [InlineData("カタカナ・ひらがな…ですか？", "カタカナ/ひらがな、ですか？")]
    [InlineData("（ゆっくり）『していってね』", "ゆっくりしていってね")]
    [InlineData("きょ'うわ/い'い/てんき", "きょ'うわ/い'い/てんき")]
    public void ConvertsKanaText(string text, string expected) => Assert.Equal(expected, KoeText.FromText(text));

    [Fact]
    public void AsksForAReadingWhenTheTextHasKanji()
    {
        var error = Assert.Throws<VoiceEngineException>(() => KoeText.FromText("今日は良い天気ABC"));

        Assert.Contains("「今日良天気ABC」", error.Message);
        Assert.Contains("読み", error.Message);
    }
}
