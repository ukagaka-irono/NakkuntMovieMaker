using System.Collections.Immutable;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Engine;

/// <summary>
/// シーンの音を AudioPlan にする（映像の TimelineEvaluator にあたるもの）。
/// ミュートのレイヤーは鳴らさず、ソロのレイヤーが 1 つでもあれば、ソロのレイヤーだけを鳴らす。
/// セリフは、録音した声なら素材を、合成した声ならキャッシュ（まだ合成していなければ鳴らさない）を鳴らす
/// </summary>
public static class AudioEvaluator
{
    public static AudioPlan Build(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var format = scene.Format;
        var anySolo = scene.Layers.Any(layer => layer.Solo);
        var audible = scene.Layers.Where(layer => !layer.Muted && (!anySolo || layer.Solo)).Select(layer => layer.Id).ToHashSet();
        var items = scene.Items.Values
            .Where(item => audible.Contains(item.Layer))
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id.Value)
            .ToList();

        var clips = items.Select(item => ClipFor(item, format)).OfType<AudioClip>().ToImmutableArray();
        var triggers = items
            .OfType<VoiceItem>()
            .Select(voice => new SampleRange(Sample(voice.Start, format), Sample(voice.End, format)))
            .ToImmutableArray();
        return new AudioPlan(format.SampleRate, format.Fps, clips, triggers, Sample(scene.Duration, format));
    }

    /// <summary>アイテムの音。音を持たないアイテムは null</summary>
    public static AudioClip? ClipFor(Item item, SceneFormat format)
    {
        (AssetId Asset, MediaPlayback Playback, AudioMix Mix)? sound = item switch
        {
            VideoItem video => (video.Asset, video.Playback, video.Audio),
            AudioItem audio => (audio.Asset, audio.Playback, audio.Audio),
            VoiceItem { Source: RecordedVoice recorded } voice => (recorded.Asset, MediaPlayback.Normal, voice.Audio),
            VoiceItem { Source: SynthesizedVoice { CacheKey: not null } } voice => (default(AssetId), MediaPlayback.Normal, voice.Audio),
            _ => null,
        };
        if (sound is not { } found)
        {
            return null;
        }

        var voiceKey = item is VoiceItem { Source: SynthesizedVoice { CacheKey: { } key } } ? key : null;

        var (asset, playback, mix) = found;

        var start = Sample(item.Start, format);
        return new AudioClip(
            item.Id,
            asset,
            start,
            Sample(item.End, format) - start,
            playback.SourceOffset,
            playback.Speed,
            playback.Reverse,
            mix.PreservePitch,
            mix.Volume,
            mix.Pan,
            TimeMath.FramesToSampleCount(item.Start, Math.Min(mix.FadeInFrames, item.Length), format.Fps, format.SampleRate),
            TimeMath.FramesToSampleCount(Math.Max(item.Start, item.End - mix.FadeOutFrames), Math.Min(mix.FadeOutFrames, item.Length), format.Fps, format.SampleRate),
            mix.DuckingDb)
        {
            VoiceKey = voiceKey,
        };
    }

    /// <summary>
    /// アイテムの音（音量 1・フェードとダッキングなし）を測って、ラウドネスを target LUFS にそろえる操作。
    /// 音量のキーフレームは形を保ったまま、全体に同じ倍率を掛ける。音がない・無音のアイテムは null
    /// </summary>
    public static IOperation? NormalizeLoudness(Project project, SceneId sceneId, ItemId itemId, IAudioSource source, double target)
    {
        var scene = project.GetScene(sceneId);
        var item = scene.GetItem(itemId);
        if (ClipFor(item, scene.Format) is not { } clip)
        {
            return null;
        }

        var plain = clip with { Volume = 1.0, FadeIn = 0, FadeOut = 0, DuckingDb = 0 };
        var plan = new AudioPlan(scene.Format.SampleRate, scene.Format.Fps, [plain], [], plain.End);
        var samples = AudioMixdown.Render(plan, source, plain.Start, plain.End);
        var measured = Loudness.Integrated(samples, plan.SampleRate);
        if (!double.IsFinite(measured))
        {
            return null;
        }

        var gain = Loudness.GainTo(measured, target);
        Animatable<double> Scale(Animatable<double> volume) =>
            volume.IsAnimated
                ? Animatable.FromKeys(volume.Keys.Select(key => key with { Value = key.Value * gain }))
                : gain;

        Item updated = item switch
        {
            VideoItem video => video with { Audio = video.Audio with { Volume = Scale(video.Audio.Volume) } },
            AudioItem audio => audio with { Audio = audio.Audio with { Volume = Scale(audio.Audio.Volume) } },
            VoiceItem voice => voice with { Audio = voice.Audio with { Volume = Scale(voice.Audio.Volume) } },
            _ => item,
        };
        return EditCommands.UpdateItem(project, sceneId, updated);
    }

    private static long Sample(long frame, SceneFormat format) => TimeMath.FrameToSample(frame, format.Fps, format.SampleRate);
}
