using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering.Effects;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

/// <summary>
/// エフェクトをかけた結果のキー。Identity は素材（画像なら読み込んだ画像そのもの、文字・図形なら中身）、Scale はかけたときの細かさ。
/// エフェクトはローカル座標でかけるので、位置・回転・不透明度が変わっても同じ結果を使える
/// </summary>
internal sealed record EffectKey(object Identity, Crop Crop, EquatableArray<EvaluatedEffect> Effects, float Scale);

/// <summary>エフェクトをかけた結果（変わらない画像と、ローカル座標での範囲）</summary>
internal sealed record EffectResult(SKImage Image, SKRect LocalBounds);

/// <summary>
/// エフェクトをかけた結果を覚えておく（最近使ったものを残し、合計が上限を超えたら古いものから捨てる）。
/// 止まっている背景の色調補正や、動いている文字の発光を、毎フレーム計算し直さないようにする。複数のスレッドから使ってよい。
/// 捨てた画像は、ほかのスレッドが描いている途中かもしれないので、明示的には破棄しない（ガベージコレクションに任せる）
/// </summary>
internal sealed class EffectResultCache(long capacityBytes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<EffectKey, (EffectResult Result, long Bytes, long LastUse)> _entries = [];
    private long _bytes;
    private long _clock;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(EffectKey key, out EffectResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = entry with { LastUse = ++_clock };
                result = entry.Result;
                return true;
            }
        }

        result = null!;
        return false;
    }

    public void Add(EffectKey key, EffectResult result)
    {
        var bytes = (long)result.Image.Width * result.Image.Height * 4;
        if (bytes > capacityBytes / 4)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _bytes -= existing.Bytes;
            }

            _entries[key] = (result, bytes, ++_clock);
            _bytes += bytes;
            while (_bytes > capacityBytes && _entries.Count > 1)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastUse);
                _entries.Remove(oldest.Key);
                _bytes -= oldest.Value.Bytes;
            }
        }
    }
}
