using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Editing;

/// <summary>
/// アイテムの長さを変える処理。トリムしても映像の中身がずれないように、
/// キーフレーム（アイテムの先頭からの相対位置）と動画・音声の素材の位置を合わせて動かす
/// </summary>
public static class ItemTiming
{
    /// <summary>アイテムの先頭を newStart に動かす（終わりは変えない）</summary>
    public static Item TrimStart(Item item, long newStart, Rational fps)
    {
        var delta = newStart - item.Start;
        if (item.Length - delta < 1)
        {
            throw new EditException("アイテムの長さは 1 フレーム以上にしてください");
        }

        var trimmed = ShiftKeyframes(item, -delta) with { Start = newStart, Length = item.Length - delta };
        // 順再生なら素材の使い始めが動く。逆再生なら先頭は素材の終わり側なので、素材の位置は変わらない
        return AdjustSourceOffset(trimmed, playback => playback.Reverse ? Rational.Zero : delta, fps);
    }

    /// <summary>アイテムの終わりを newEnd に動かす（先頭は変えない）</summary>
    public static Item TrimEnd(Item item, long newEnd, Rational fps)
    {
        var newLength = newEnd - item.Start;
        if (newLength < 1)
        {
            throw new EditException("アイテムの長さは 1 フレーム以上にしてください");
        }

        var trimmed = item with { Length = newLength };
        // 逆再生なら終わりは素材の使い始め側なので、終わりを削った分だけ素材の位置が動く
        var removed = item.End - newEnd;
        return AdjustSourceOffset(trimmed, playback => playback.Reverse ? removed : Rational.Zero, fps);
    }

    /// <summary>アイテムを frame で 2 つに分ける。後ろ側は rightId の新しいアイテムになる</summary>
    public static (Item Left, Item Right) Split(Item item, long frame, ItemId rightId, Rational fps)
    {
        if (item is VoiceItem)
        {
            throw new EditException("セリフは分割できません");
        }

        if (frame <= item.Start || frame >= item.End)
        {
            throw new EditException("アイテムの途中のフレームを指定してください");
        }

        var left = TrimEnd(item, frame, fps);
        var right = TrimStart(item, frame, fps) with { Id = rightId };
        return (left, right);
    }

    /// <summary>アイテムのすべてのキーフレームを delta フレームずらす</summary>
    public static Item ShiftKeyframes(Item item, long delta)
    {
        if (delta == 0)
        {
            return item;
        }

        var shifted = item with { Effects = [.. item.Effects.Select(effect => ShiftEffect(effect, delta))] };

        if (shifted is VisualItem visual)
        {
            shifted = visual with
            {
                Transform = ShiftTransform(visual.Transform, delta),
                Opacity = visual.Opacity.ShiftKeys(delta),
            };
        }

        return shifted switch
        {
            VideoItem video => video with { Audio = ShiftAudio(video.Audio, delta) },
            AudioItem audio => audio with { Audio = ShiftAudio(audio.Audio, delta) },
            VoiceItem voice => voice with { Audio = ShiftAudio(voice.Audio, delta) },
            _ => shifted,
        };
    }

    // 素材の位置を、削ったフレーム数 × (速度 ÷ フレームレート) 秒だけ動かす
    private static Item AdjustSourceOffset(Item item, Func<MediaPlayback, Rational> removedFrames, Rational fps)
    {
        MediaPlayback Adjust(MediaPlayback playback)
        {
            var offset = playback.SourceOffset + removedFrames(playback) * playback.Speed / fps;
            if (offset < Rational.Zero)
            {
                throw new EditException("素材の先頭より前には伸ばせません");
            }

            return playback with { SourceOffset = offset };
        }

        return item switch
        {
            VideoItem video => video with { Playback = Adjust(video.Playback) },
            AudioItem audio => audio with { Playback = Adjust(audio.Playback) },
            _ => item,
        };
    }

    private static Transform2D ShiftTransform(Transform2D transform, long delta) => transform with
    {
        X = transform.X.ShiftKeys(delta),
        Y = transform.Y.ShiftKeys(delta),
        ScaleX = transform.ScaleX.ShiftKeys(delta),
        ScaleY = transform.ScaleY.ShiftKeys(delta),
        Rotation = transform.Rotation.ShiftKeys(delta),
        AnchorX = transform.AnchorX.ShiftKeys(delta),
        AnchorY = transform.AnchorY.ShiftKeys(delta),
    };

    private static AudioMix ShiftAudio(AudioMix audio, long delta) => audio with
    {
        Volume = audio.Volume.ShiftKeys(delta),
        Pan = audio.Pan.ShiftKeys(delta),
    };

    private static EffectInstance ShiftEffect(EffectInstance effect, long delta) => effect with
    {
        Parameters = effect.Parameters.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value switch
            {
                NumberParameter number => new NumberParameter(number.Value.ShiftKeys(delta)),
                ColorParameter color => new ColorParameter(color.Value.ShiftKeys(delta)),
                var other => other,
            }),
    };
}
