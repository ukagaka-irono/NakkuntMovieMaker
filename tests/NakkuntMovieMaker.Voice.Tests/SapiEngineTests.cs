using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Voice.Sapi;

namespace NakkuntMovieMaker.Voice.Tests;

/// <summary>
/// SAPI。Windows で声が入っているときだけ合成を確かめる（それ以外の環境ではスキップする）
/// </summary>
public sealed class SapiEngineTests : IDisposable
{
    private readonly SapiEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    private void RequireSapi() =>
        Assert.SkipUnless(_engine.Status.Available, $"SAPI を使えないためスキップします（{_engine.Status.Message}）");

    // 合成は STA のスレッドで行う
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        thread.Join();
        return error is null ? result : throw error;
    }

    private VoiceAudio Speak(string text, VoiceParams parameters) =>
        OnSta(() => _engine.Synthesize(new VoiceRequest(text, null, SapiEngine.DefaultVoice, parameters), CancellationToken.None));

    private static float Peak(VoiceAudio audio) => audio.Samples.Max(Math.Abs);

    // 既定の声が日本語とは限らない（GitHub Actions の Windows には英語の声しかなく、日本語の文は読まずに無音になる）。
    // 英語の文なら、英語の声でも日本語の声でも読める
    private const string Sentence = "Hello. This is a test of the speech engine.";

    [Fact]
    public void ReportsAvailabilityAndVoices()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(_engine.Status.Available);
            Assert.Empty(_engine.Voices);
            return;
        }

        RequireSapi();
        Assert.Equal(SapiEngine.DefaultVoice, _engine.Voices[0].Id);
        Assert.True(_engine.Voices.Count >= 2);
        Assert.True(_engine.RequiresSingleThreadedApartment);
        Assert.Null(_engine.MakeReading("今日は"));
    }

    [Fact]
    public void SynthesizesSpeech()
    {
        RequireSapi();

        var audio = Speak(Sentence, VoiceParams.Default);

        Assert.Equal(48000, audio.SampleRate);
        Assert.True(audio.Duration > TimeSpan.FromSeconds(0.3), $"短すぎる: {audio.Duration}");
        Assert.True(Peak(audio) > 0.01f, "無音になっている");
    }

    [Fact]
    public void FollowsSpeedAndVolume()
    {
        RequireSapi();
        const string text = Sentence;

        var normal = Speak(text, VoiceParams.Default);
        var fast = Speak(text, VoiceParams.Default with { Speed = 2 });
        var quiet = Speak(text, VoiceParams.Default with { Volume = 0.5 });

        Assert.True(fast.Duration.TotalSeconds < normal.Duration.TotalSeconds * 0.8, $"速くならない: {normal.Duration} → {fast.Duration}");
        Assert.InRange(Peak(quiet) / Peak(normal), 0.4, 0.6);
    }

    [Fact]
    public async Task WorksThroughTheVoiceServiceOnItsStaThread()
    {
        RequireSapi();
        var folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new VoiceService(new VoiceEngineRegistry([new SapiEngine()]), new VoiceCache(folder));

            var result = await service.GetAsync(new VoiceSpec(SapiEngine.EngineId, SapiEngine.DefaultVoice, VoiceParams.Default), "テスト", null, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(result.Path));
            Assert.Equal("", result.Reading);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(3.0, 10)]
    [InlineData(1 / 3.0, -10)]
    [InlineData(10.0, 10)] // 範囲の外は端に収める
    [InlineData(1.5, 4)]
    public void MapsSpeedToSapiRate(double speed, int rate) => Assert.Equal(rate, SapiEngine.ToRate(speed));

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(2.0, 10)]
    [InlineData(0.5, -10)]
    [InlineData(1.2, 3)]
    public void MapsPitchToSapiPitch(double pitch, int value) => Assert.Equal(value, SapiEngine.ToPitch(pitch));
}
