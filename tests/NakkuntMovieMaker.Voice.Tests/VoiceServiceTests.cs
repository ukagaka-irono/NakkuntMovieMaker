using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Voice.Tests;

public sealed class VoiceServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
    private readonly ToneEngine _engine = new();
    private readonly VoiceService _service;

    public VoiceServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _service = new VoiceService(new VoiceEngineRegistry([_engine]), new VoiceCache(_folder));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static VoiceSpec Voice(string voice = "a", VoiceParams? parameters = null) => new("tone", voice, parameters ?? VoiceParams.Default);

    public void Dispose()
    {
        _service.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task SynthesizesOnceAndThenUsesTheCache()
    {
        var first = await _service.GetAsync(Voice(), "こんにちは", reading: null, Token);
        var second = await _service.GetAsync(Voice(), "こんにちは", reading: null, Token);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.CacheKey, second.CacheKey);
        Assert.Equal(1, _engine.Calls);
        Assert.True(File.Exists(first.Path));
        Assert.Equal(Path.Combine(_folder, "voices", first.CacheKey + ".wav"), first.Path);

        // 読み「ヨミ:こんにちは」は 8 文字なので 0.8 秒
        Assert.Equal(TimeSpan.FromSeconds(0.8), first.Duration);
        Assert.Equal(TimeSpan.FromSeconds(0.8), second.Duration);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "voices"), "*.tmp"));
    }

    [Fact]
    public async Task KeyDependsOnEverythingThatChangesTheVoice()
    {
        var keys = new[]
        {
            (await _service.GetAsync(Voice(), "あ", null, Token)).CacheKey,
            (await _service.GetAsync(Voice("b"), "あ", null, Token)).CacheKey,
            (await _service.GetAsync(Voice(parameters: VoiceParams.Default with { Speed = 1.2 }), "あ", null, Token)).CacheKey,
            (await _service.GetAsync(Voice(), "い", null, Token)).CacheKey,
            (await _service.GetAsync(Voice(), "あ", "ア'", Token)).CacheKey,
        };
        _engine.Version = "2";
        var upgraded = (await _service.GetAsync(Voice(), "あ", null, Token)).CacheKey;

        Assert.Equal(6, keys.Append(upgraded).Distinct().Count());
        Assert.Equal(6, _engine.Calls);
    }

    [Fact]
    public async Task UsesTheGivenReadingOrMakesOne()
    {
        var automatic = await _service.GetAsync(Voice(), "今日", reading: null, Token);
        var manual = await _service.GetAsync(Voice(), "今日", reading: "キョー", Token);

        Assert.Equal("ヨミ:今日", automatic.Reading);
        Assert.Equal("キョー", manual.Reading);
        Assert.Equal(["ヨミ:今日", "キョー"], _engine.Requests.Select(request => request.Reading));
    }

    [Fact]
    public async Task CallsEachEngineFromOneThreadInOrder()
    {
        var other = new ToneEngine("other");
        using var service = new VoiceService(new VoiceEngineRegistry([_engine, other]), new VoiceCache(_folder));
        _engine.Delay = TimeSpan.FromMilliseconds(20);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => service.GetAsync(Voice(), $"文{i}", null, Token))
            .Append(service.GetAsync(new VoiceSpec("other", "a", VoiceParams.Default), "別", null, Token)));

        Assert.Equal(9, results.Length);
        Assert.Equal(1, _engine.MaxConcurrency);
        Assert.Single(_engine.Threads);
        Assert.NotEqual(_engine.Threads.Single(), other.Threads.Single());
    }

    [Fact]
    public async Task ReportsMissingUnavailableAndFailingEngines()
    {
        await Assert.ThrowsAsync<VoiceEngineException>(() => _service.GetAsync(new VoiceSpec("aquestalk1", "f1", VoiceParams.Default), "あ", null, Token));

        _engine.Status = new VoiceEngineStatus(false, "AquesTalk は導入されていません", "https://www.a-quest.com/");
        var unavailable = await Assert.ThrowsAsync<VoiceEngineException>(() => _service.GetAsync(Voice(), "あ", null, Token));
        Assert.Equal("AquesTalk は導入されていません", unavailable.Message);

        _engine.Status = VoiceEngineStatus.Ready;
        _engine.Fail = true;
        var failed = await Assert.ThrowsAsync<VoiceEngineException>(() => _service.GetAsync(Voice(), "失敗", null, Token));
        Assert.Contains("合成できませんでした", failed.Message);
    }

    [Fact]
    public async Task CancelledRequestsAreNotSynthesized()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetAsync(Voice(), "あ", null, cancel.Token));
        Assert.Equal(0, _engine.Calls);
    }

    [Fact]
    public void CacheKeepsTheVoiceAs16BitWav()
    {
        var cache = new VoiceCache(_folder);
        var samples = Enumerable.Range(0, 1000).Select(i => (float)Math.Sin(i * 0.05) * 0.8f).ToArray();

        cache.Save("abc", new VoiceAudio(samples, 22050));
        var loaded = cache.Load("abc");

        Assert.NotNull(loaded);
        Assert.Equal(22050, loaded.SampleRate);
        Assert.Equal(samples.Length, loaded.Samples.Length);
        Assert.All(samples.Zip(loaded.Samples), pair => Assert.Equal(pair.First, pair.Second, 1e-4));
        Assert.Null(cache.Load("missing"));
    }
}
