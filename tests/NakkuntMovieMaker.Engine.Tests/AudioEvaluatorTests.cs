using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Engine.Tests;

public class AudioEvaluatorTests
{
    // 30fps・48kHz：1 フレームは 1600 サンプル。レイヤーは 3 つ
    private static readonly Scene Empty = Scene.CreateNew("テスト", SceneFormat.Landscape1080p30, 3);

    private static Scene With(params Item[] items) =>
        Empty with { Items = Empty.Items.SetItems(items.Select(item => KeyValuePair.Create(item.Id, item))) };

    private static AudioItem Audio(long start, long length, int layer = 0, AssetId? asset = null) =>
        new(ItemId.New(), Empty.Layers[layer].Id, start, length, asset ?? AssetId.New());

    [Fact]
    public void ConvertsItemsToClipsInSamples()
    {
        var audio = Audio(30, 60) with
        {
            Playback = new MediaPlayback(new Rational(1, 2), 2, Reverse: true),
            Audio = new AudioMix(0.5, -0.25, FadeInFrames: 3, FadeOutFrames: 6, PreservePitch: false, DuckingDb: 9),
        };
        var video = new VideoItem(ItemId.New(), Empty.Layers[1].Id, 0, 30, AssetId.New());
        var image = new ImageItem(ItemId.New(), Empty.Layers[2].Id, 0, 30, AssetId.New());

        var plan = AudioEvaluator.Build(With(audio, video, image));

        Assert.Equal((48000, 90 * 1600L), (plan.SampleRate, plan.Length));
        Assert.Equal(2, plan.Clips.Length); // 画像は音を持たない
        var clip = plan.Clips.Single(candidate => candidate.Item == audio.Id);
        Assert.Equal((48000L, 96000L), (clip.Start, clip.Length));
        Assert.Equal((new Rational(1, 2), new Rational(2, 1), true, false), (clip.SourceOffset, clip.Speed, clip.Reverse, clip.PreservePitch));
        Assert.Equal((4800L, 9600L, 9.0), (clip.FadeIn, clip.FadeOut, clip.DuckingDb));
        Assert.Equal(Animatable.Constant(0.5), clip.Volume);
    }

    [Fact]
    public void MutedLayersAreSilentAndSoloLayersPlayAlone()
    {
        var first = Audio(0, 30, layer: 0);
        var second = Audio(0, 30, layer: 1);
        var scene = With(first, second);

        var muted = scene with { Layers = scene.Layers.SetItem(0, scene.Layers[0] with { Muted = true }) };
        Assert.Equal([second.Id], AudioEvaluator.Build(muted).Clips.Select(clip => clip.Item));

        var solo = scene with { Layers = scene.Layers.SetItem(0, scene.Layers[0] with { Solo = true }) };
        Assert.Equal([first.Id], AudioEvaluator.Build(solo).Clips.Select(clip => clip.Item));

        // 映像だけを隠しても、音は鳴る
        var hidden = scene with { Layers = scene.Layers.SetItem(0, scene.Layers[0] with { Visible = false }) };
        Assert.Equal(2, AudioEvaluator.Build(hidden).Clips.Length);
    }

    [Fact]
    public void VoicesTriggerDuckingAndRecordedVoicesPlay()
    {
        var character = CharacterId.New();
        var synthesized = new VoiceItem(ItemId.New(), Empty.Layers[1].Id, 30, 30, character, "こんにちは");
        var recorded = new VoiceItem(ItemId.New(), Empty.Layers[1].Id, 90, 15, character, "録音") { Source = new RecordedVoice(AssetId.New()) };

        var plan = AudioEvaluator.Build(With(synthesized, recorded));

        Assert.Equal([new SampleRange(48000, 96000), new SampleRange(144000, 168000)], plan.DuckingTriggers);
        Assert.Equal([recorded.Id], plan.Clips.Select(clip => clip.Item)); // 音声合成の声は、セリフの手順で加える
    }

    [Fact]
    public void UsesTheSampleOfTheFrameStartAt2997Fps()
    {
        var scene = Scene.CreateNew("テスト", SceneFormat.Landscape1080p30 with { Fps = Rational.Fps29_97 }, 1);
        var audio = new AudioItem(ItemId.New(), scene.Layers[0].Id, 1, 1, AssetId.New());

        var clip = Assert.Single(AudioEvaluator.Build(scene with { Items = scene.Items.Add(audio.Id, audio) }).Clips);

        // 1 フレーム目は 1601.6 サンプル目から始まるので 1602 から、2 フレーム目（3203.2）の手前 3203 まで
        Assert.Equal((1602L, 1602L), (clip.Start, clip.Length));
    }

    [Fact]
    public void NormalizeLoudnessScalesTheVolume()
    {
        // −30 LUFS の音を −16 LUFS にそろえる：音量は 14dB（約 5 倍）
        var source = new FixedAudio(Sine(1000, Math.Pow(10, -30 / 20.0), 4));
        var item = Audio(0, 120, asset: source.Asset) with
        {
            Audio = AudioMix.Default with { Volume = Animatable.FromKeys(new Keyframe<double>(0, 1), new Keyframe<double>(60, 0.5)) },
        };
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        project = project.WithScene(project.Root with { Items = project.Root.Items.Add(item.Id, item), Layers = Empty.Layers });

        var operation = AudioEvaluator.NormalizeLoudness(project, project.RootScene, item.Id, source, -16);
        var normalized = (AudioItem)operation!.Apply(project).Project.Root.GetItem(item.Id);

        var gain = Math.Pow(10, 14 / 20.0);
        Assert.Equal(gain, normalized.Audio.Volume.Keys[0].Value, gain * 0.02);
        Assert.Equal(gain * 0.5, normalized.Audio.Volume.Keys[1].Value, gain * 0.02); // キーフレームの形は保つ
        Assert.Null(AudioEvaluator.NormalizeLoudness(project with { }, project.RootScene, item.Id, new FixedAudio(new float[96000]), -16));
    }

    private static float[] Sine(double frequency, double amplitude, double seconds)
    {
        var frames = (int)(seconds * 48000);
        return [.. Enumerable.Range(0, frames).SelectMany(i => Enumerable.Repeat((float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 48000)), 2))];
    }

    // どの素材にも同じ音を返す
    private sealed class FixedAudio(float[] samples) : IAudioSource
    {
        public AssetId Asset { get; } = AssetId.New();

        public IAudioStream? OpenAudio(AssetId asset) => new MemoryAudioStream(samples, 2, 48000);
    }
}
