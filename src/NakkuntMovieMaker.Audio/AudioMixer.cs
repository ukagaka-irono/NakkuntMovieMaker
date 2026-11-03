using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Audio;

/// <summary>
/// AudioPlan に従って音を混ぜる。出力はステレオ（インターリーブの float32）で、先頭から順に Render で作る。
/// 途中の位置から始めるときは Seek を呼ぶ。
/// <list type="bullet">
/// <item>音量：キーフレーム × フェード（直線）× ダッキング</item>
/// <item>パン：ステレオのバランス（中央で左右とも元の大きさ、右に振ると左が小さくなる）。モノラルは左右に同じ音を出してから振る</item>
/// <item>速度：音の高さを保つときは WSOLA、保たないときは補間で読み飛ばす（3 次の補間）</item>
/// </list>
/// 書き出しでは先頭から順に作るので、結果は毎回同じになる。スレッドセーフではない
/// </summary>
public sealed class AudioMixer
{
    /// <summary>ダッキングで下げ始める時間（セリフの始まりより前から下げる）</summary>
    public static readonly TimeSpan DuckingAttack = TimeSpan.FromMilliseconds(150);

    /// <summary>ダッキングを戻すのにかける時間</summary>
    public static readonly TimeSpan DuckingRelease = TimeSpan.FromMilliseconds(400);

    // 音量・パンは、これだけのサンプルごとに計算して、その間は直線でつなぐ
    private const int EnvelopeStep = 32;

    private readonly IAudioSource _source;
    private readonly Dictionary<AudioClip, ClipVoice?> _voices = new(ReferenceEqualityComparer.Instance);
    private AudioPlan _plan;
    private float[] _clipBuffer = [];

    public AudioMixer(AudioPlan plan, IAudioSource source)
    {
        _plan = plan;
        _source = source;
    }

    public AudioPlan Plan => _plan;

    /// <summary>次に作るサンプルの位置（タイムラインのサンプル）</summary>
    public long Position { get; private set; }

    /// <summary>次に作る位置を変える（素材の読み出しは、次の Render で位置に合わせ直す）</summary>
    public void Seek(long sample) => Position = sample;

    /// <summary>編集した後の AudioPlan にする。変わらなかったアイテムの状態は引き継ぐ</summary>
    public void SetPlan(AudioPlan plan)
    {
        var kept = _voices.Where(pair => plan.Clips.Contains(pair.Key)).ToList();
        _voices.Clear();
        foreach (var (clip, voice) in kept)
        {
            var current = plan.Clips.First(candidate => candidate == clip);
            _voices[current] = voice;
        }

        _plan = plan;
    }

    /// <summary>続きのサンプルを output（ステレオ）に書く</summary>
    public void Render(Span<float> output)
    {
        if (output.Length % 2 != 0)
        {
            throw new ArgumentException("ステレオなので、長さは 2 の倍数にしてください", nameof(output));
        }

        output.Clear();
        var frames = output.Length / 2;
        var blockStart = Position;
        var blockEnd = Position + frames;
        foreach (var clip in _plan.Clips)
        {
            var start = Math.Max(clip.Start, blockStart);
            var end = Math.Min(clip.End, blockEnd);
            if (start >= end)
            {
                continue;
            }

            if (!_voices.TryGetValue(clip, out var voice))
            {
                voice = ClipVoice.Create(clip, _plan, _source);
                _voices[clip] = voice;
            }

            if (voice is null)
            {
                continue;
            }

            var count = (int)(end - start);
            if (_clipBuffer.Length < count * 2)
            {
                _clipBuffer = new float[count * 2];
            }

            var buffer = _clipBuffer.AsSpan(0, count * 2);
            voice.Read(start - clip.Start, buffer);
            ApplyEnvelope(clip, start, buffer, output[(int)((start - blockStart) * 2)..]);
        }

        Position = blockEnd;
    }

    /// <summary>タイムラインのサンプル sample での、アイテムの音量（キーフレーム × フェード × ダッキング）</summary>
    public double GainAt(AudioClip clip, long sample)
    {
        var local = sample - clip.Start;
        var gain = Math.Max(0, clip.Volume.Evaluate(LocalFrame(clip, sample)));
        if (clip.FadeIn > 0 && local < clip.FadeIn)
        {
            gain *= (double)local / clip.FadeIn;
        }

        var remaining = clip.Length - local;
        if (clip.FadeOut > 0 && remaining < clip.FadeOut)
        {
            gain *= (double)remaining / clip.FadeOut;
        }

        if (clip.DuckingDb > 0)
        {
            gain *= Math.Pow(10, -clip.DuckingDb * DuckingAmount(sample) / 20);
        }

        return gain;
    }

    /// <summary>
    /// サンプル sample で、ダッキングをどれだけかけるか（0〜1）。セリフの始まりの少し前から下げ始め、終わってから戻す
    /// </summary>
    public double DuckingAmount(long sample)
    {
        var attack = DuckingAttack.TotalSeconds * _plan.SampleRate;
        var release = DuckingRelease.TotalSeconds * _plan.SampleRate;
        var amount = 0.0;
        foreach (var trigger in _plan.DuckingTriggers)
        {
            var value = sample < trigger.Start ? 1 - (trigger.Start - sample) / attack
                : sample < trigger.End ? 1
                : 1 - (sample - trigger.End) / release;
            amount = Math.Max(amount, Math.Clamp(value, 0, 1));
        }

        return amount;
    }

    // タイムラインのサンプルを、アイテムの先頭からのフレーム（小数）にする
    private double LocalFrame(AudioClip clip, long sample) => (sample - clip.Start) * _plan.Fps.ToDouble() / _plan.SampleRate;

    // 音量とパンをかけて output に足す。音量・パンは EnvelopeStep ごとに求めて、間は直線でつなぐ
    private void ApplyEnvelope(AudioClip clip, long start, ReadOnlySpan<float> samples, Span<float> output)
    {
        var frames = samples.Length / 2;
        (float Left, float Right) Gains(long sample)
        {
            var gain = GainAt(clip, sample);
            var pan = Math.Clamp(clip.Pan.Evaluate(LocalFrame(clip, sample)), -1, 1);
            return ((float)(gain * (pan > 0 ? 1 - pan : 1)), (float)(gain * (pan < 0 ? 1 + pan : 1)));
        }

        var current = Gains(start);
        for (var block = 0; block < frames; block += EnvelopeStep)
        {
            var length = Math.Min(EnvelopeStep, frames - block);
            var next = Gains(start + block + length);
            for (var i = 0; i < length; i++)
            {
                var t = (float)i / length;
                var left = current.Left + (next.Left - current.Left) * t;
                var right = current.Right + (next.Right - current.Right) * t;
                output[(block + i) * 2] += samples[(block + i) * 2] * left;
                output[(block + i) * 2 + 1] += samples[(block + i) * 2 + 1] * right;
            }

            current = next;
        }
    }

    /// <summary>1 つのアイテムの素材を、アイテムの先頭からの位置で読む（速度・逆再生・音の高さの保持を扱う）</summary>
    private sealed class ClipVoice
    {
        private readonly ClipSourceView _view;
        private readonly double _step;
        private readonly TimeStretcher? _stretcher;
        private float[] _source = [];

        private ClipVoice(ClipSourceView view, double step, TimeStretcher? stretcher)
        {
            _view = view;
            _step = step;
            _stretcher = stretcher;
        }

        public static ClipVoice? Create(AudioClip clip, AudioPlan plan, IAudioSource source)
        {
            if (source.OpenAudio(clip.Asset) is not { } stream)
            {
                return null;
            }

            // 素材のサンプルで、アイテムの 1 サンプルあたりに進む量と、使う範囲
            var step = clip.Speed.ToDouble() * stream.SampleRate / plan.SampleRate;
            var start = (clip.SourceOffset * stream.SampleRate).ToInt64(Rounding.Nearest);
            var length = (long)Math.Round(clip.Length * step);
            var view = new ClipSourceView(stream, start, length, clip.Reverse);

            // 音の高さを保つ処理は、サンプルレートが同じときだけ使う（違うときは読み飛ばしで高さも変わる）
            var stretch = clip.PreservePitch && Math.Abs(step - 1) > 1e-9 && stream.SampleRate == plan.SampleRate;
            return new ClipVoice(view, step, stretch ? new TimeStretcher(view, step) : null);
        }

        /// <summary>アイテムの先頭から local フレーム目から、destination（ステレオ）の長さぶん読む</summary>
        public void Read(long local, Span<float> destination)
        {
            var frames = destination.Length / 2;
            if (_stretcher is not null)
            {
                if (_stretcher.Position != local)
                {
                    _stretcher.Reset(local);
                }

                _stretcher.Read(destination);
                return;
            }

            if (Math.Abs(_step - 1) < 1e-9)
            {
                _view.ReadStereo(local, destination);
                return;
            }

            // 素材の位置 local × step の前後を読んで、3 次の補間（Catmull-Rom）で値を求める
            var first = (long)Math.Floor(local * _step) - 1;
            var last = (long)Math.Floor((local + frames - 1) * _step) + 2;
            var count = (int)(last - first + 1);
            if (_source.Length < count * 2)
            {
                _source = new float[count * 2];
            }

            var source = _source.AsSpan(0, count * 2);
            _view.ReadStereo(first, source);
            for (var i = 0; i < frames; i++)
            {
                var position = (local + i) * _step;
                var index = (long)Math.Floor(position);
                var t = (float)(position - index);
                var at = (int)(index - first);
                for (var channel = 0; channel < 2; channel++)
                {
                    var p0 = source[(at - 1) * 2 + channel];
                    var p1 = source[at * 2 + channel];
                    var p2 = source[(at + 1) * 2 + channel];
                    var p3 = source[(at + 2) * 2 + channel];
                    destination[i * 2 + channel] = CatmullRom(p0, p1, p2, p3, t);
                }
            }
        }

        private static float CatmullRom(float p0, float p1, float p2, float p3, float t) =>
            0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);
    }
}
