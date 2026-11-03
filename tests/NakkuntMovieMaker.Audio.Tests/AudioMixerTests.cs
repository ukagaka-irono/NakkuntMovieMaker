using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Audio.Tests;

public class AudioMixerTests
{
    private const int Rate = Signals.Rate;

    // 30fps なので、1 フレームは 1600 サンプル
    private static readonly Rational Fps = 30;

    private readonly TestAudio _audio = new();

    private static AudioClip Clip(AssetId asset, long start, long length) =>
        new(ItemId.New(), asset, start, length, Rational.Zero, Rational.One, false, true, 1.0, 0.0, 0, 0, 0);

    private float[] Mix(long length, params AudioClip[] clips) => Mix(length, [], clips);

    private float[] Mix(long length, SampleRange[] triggers, params AudioClip[] clips) =>
        AudioMixdown.Render(new AudioPlan(Rate, Fps, [.. clips], [.. triggers], length), _audio, 0, length, TestContext.Current.CancellationToken);

    [Fact]
    public void PlacesTheSourceAtTheClipPosition()
    {
        var sine = Signals.Sine(440, 0.5, 1);
        var clip = Clip(_audio.Add(sine), 1000, 48000);

        var output = Mix(50000, clip);

        Assert.All(output[..2000], sample => Assert.Equal(0f, sample));
        Assert.Equal(sine.AsSpan(0, 96000).ToArray(), output[2000..98000]);
        Assert.All(output[98000..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void AddsOverlappingClipsAndAppliesVolumeAndPan()
    {
        var one = _audio.Add(Signals.Constant(0.25f, 4800));
        var output = Mix(4800,
            Clip(one, 0, 4800),
            Clip(one, 0, 4800) with { Volume = 0.5 },
            Clip(one, 0, 4800) with { Pan = 1.0 }); // 右に振り切ると左は鳴らない

        Assert.Equal(0.25f + 0.125f, output[1000 * 2], 5);
        Assert.Equal(0.25f + 0.125f + 0.25f, output[1000 * 2 + 1], 5);

        var half = Mix(4800, Clip(one, 0, 4800) with { Pan = -0.5 });
        Assert.Equal((0.25f, 0.125f), (half[0], half[1]));
    }

    [Fact]
    public void PlaysMonoSourcesOnBothChannels()
    {
        var mono = _audio.Add(Signals.Constant(0.3f, 4800, channels: 1), channels: 1);

        var output = Mix(4800, Clip(mono, 0, 4800));

        Assert.Equal((0.3f, 0.3f), (output[100], output[101]));
    }

    [Fact]
    public void AppliesVolumeKeyframesAndFades()
    {
        var one = _audio.Add(Signals.Constant(1f, 48000));

        // 音量は 0 フレーム目の 0 から 10 フレーム目（16000 サンプル）の 1 へ
        var keyed = Clip(one, 0, 48000) with { Volume = Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(10, 1)) };
        var ramp = Mix(48000, keyed);
        Assert.Equal(0.5f, ramp[8000 * 2], 3);
        Assert.Equal(1f, ramp[20000 * 2], 5);

        // 4800 サンプルでフェードイン・フェードアウト
        var faded = Mix(48000, Clip(one, 0, 48000) with { FadeIn = 4800, FadeOut = 4800 });
        Assert.Equal(0f, faded[0], 3);
        Assert.Equal(0.5f, faded[2400 * 2], 2);
        Assert.Equal(1f, faded[24000 * 2], 5);
        Assert.Equal(0.5f, faded[(48000 - 2400) * 2], 2);
    }

    [Fact]
    public void DucksDuringVoices()
    {
        // BGM をセリフ [24000, 48000) の間 12dB 下げる。下げ始めは 150ms（7200 サンプル）前、戻すのは 400ms（19200 サンプル）
        var bgm = Clip(_audio.Add(Signals.Constant(1f, 96000)), 0, 96000) with { DuckingDb = 12 };

        var output = Mix(96000, [new SampleRange(24000, 48000)], bgm);

        var ducked = Math.Pow(10, -12 / 20.0);
        Assert.Equal(1f, output[10000 * 2], 5);
        Assert.Equal(Math.Pow(10, -6 / 20.0), output[(24000 - 3600) * 2], 2); // 下げ始めの途中（半分の 6dB）
        Assert.Equal(ducked, output[36000 * 2], 3);
        Assert.Equal(Math.Pow(10, -6 / 20.0), output[(48000 + 9600) * 2], 2); // 戻す途中
        Assert.Equal(1f, output[80000 * 2], 5);
    }

    [Theory]
    [InlineData(2.0, 2000)]
    [InlineData(0.5, 500)]
    public void ChangesPitchWithSpeedWhenNotPreserving(double speed, int expectedHz)
    {
        var sine = _audio.Add(Signals.Sine(1000, 0.5, 4));
        var clip = Clip(sine, 0, 48000) with { Speed = new Rational((long)(speed * 2), 2), PreservePitch = false };

        var output = Mix(48000, clip);

        Assert.InRange(Signals.RisingZeroCrossings(output), expectedHz - 3, expectedHz + 3);
    }

    [Fact]
    public void KeepsPitchWhenPreserving()
    {
        // 前半 2 秒は 440Hz、後半 2 秒は 880Hz。2 倍速の 2 秒で、前半は 440Hz、後半は 880Hz に聞こえる
        float[] source = [.. Signals.Sine(440, 0.5, 2), .. Signals.Sine(880, 0.5, 2)];
        var clip = Clip(_audio.Add(source), 0, 96000) with { Speed = 2 };

        var output = Mix(96000, clip);

        Assert.InRange(Signals.RisingZeroCrossings(output.AsSpan(4800 * 2, 38400 * 2)), 440 * 0.8 - 5, 440 * 0.8 + 5);
        Assert.InRange(Signals.RisingZeroCrossings(output.AsSpan(52800 * 2, 38400 * 2)), 880 * 0.8 - 5, 880 * 0.8 + 5);
        // 音量も保つ（振幅 0.5 の正弦波の RMS は約 0.354）
        Assert.InRange(Signals.Rms(output, 10000, 40000), 0.32, 0.39);
    }

    [Fact]
    public void PlaysInReverseFromTheEndOfTheRange()
    {
        // 値が 0 から増えていく素材を、1000 サンプル目から 2000 サンプル分、逆向きに使う
        var ramp = Enumerable.Range(0, 10000).SelectMany(i => new[] { i / 10000f, i / 10000f }).ToArray();
        var clip = Clip(_audio.Add(ramp), 0, 2000) with { SourceOffset = new Rational(1000, Rate), Reverse = true };

        var output = Mix(2000, clip);

        Assert.Equal(2999 / 10000f, output[0], 5);
        Assert.Equal(1000 / 10000f, output[1999 * 2], 5);
    }

    [Fact]
    public void ResultDoesNotDependOnTheBlockSize()
    {
        float[] source = [.. Signals.Sine(440, 0.5, 2), .. Signals.Sine(660, 0.5, 2)];
        var asset = _audio.Add(source);
        var plan = new AudioPlan(Rate, Fps,
            [
                Clip(asset, 0, 60000) with { Speed = new Rational(3, 2) },
                Clip(asset, 30000, 50000) with { Speed = new Rational(4, 5), PreservePitch = false, Volume = 0.5 },
            ],
            [],
            90000);

        var whole = AudioMixdown.Render(plan, _audio, 0, 90000, TestContext.Current.CancellationToken);
        var mixer = new AudioMixer(plan, _audio);
        var pieces = new float[90000 * 2];
        for (var offset = 0; offset < pieces.Length;)
        {
            var count = Math.Min(pieces.Length - offset, (offset / 2 % 7 + 1) * 333 * 2);
            mixer.Render(pieces.AsSpan(offset, count));
            offset += count;
        }

        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void SkipsClipsWithoutAudio()
    {
        var output = Mix(1000, Clip(AssetId.New(), 0, 1000));

        Assert.All(output, sample => Assert.Equal(0f, sample));
    }
}
