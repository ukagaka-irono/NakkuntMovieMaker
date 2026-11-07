using System.Diagnostics;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Engine.Playback;

/// <summary>
/// 再生の設定。BufferMilliseconds は先に混ぜておく音の長さ（長いほど途切れにくいが、編集・シークの反映が遅れる）、
/// PrefetchFrames は先に描いておくフレームの数。NullAudioDevice は音を出さない仮のデバイスを使う（テスト用）、
/// DisableAudioDevice はデバイスを使わず、時計だけで再生する。OutputDevice は出力デバイスの名前（null なら既定のデバイス）
/// </summary>
public sealed record PlaybackOptions(
    int BufferMilliseconds = 150,
    int PrefetchFrames = 12,
    bool NullAudioDevice = false,
    bool DisableAudioDevice = false,
    string? OutputDevice = null);

/// <summary>再生の記録。DroppedFrames は、表示する時刻までに描き終わらず飛ばしたフレームの数</summary>
public sealed record PlaybackStatistics(long ShownFrames, long DroppedFrames, long UnderrunFrames);

/// <summary>
/// シーンを再生する。音はこのクラスのスレッドで混ぜてデバイスのリングバッファに書き込み、
/// デバイスが実際に鳴らした量を時計にする（音が途切れたら時計も止まるので、映像と音がずれない）。
/// 映像は、画面の更新のたびに TryGetFrame で時計の位置のフレームを取り出す。描き終わっていなければ飛ばす。
/// 音のデバイスが使えないときは、ストップウォッチを時計にして映像だけ再生する。
/// 複数のスレッドから呼んでよい
/// </summary>
public sealed class PlaybackController : IDisposable
{
    private const int BlockFrames = 1024;

    private readonly Lock _gate = new();
    private readonly FrameScheduler _frames;
    private readonly PlaybackOptions _options;
    private readonly AudioOutput? _output;
    private readonly AudioMixer _mixer;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _running = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly float[] _block = new float[BlockFrames * 2];
    private AudioPlan _plan;
    private bool _playing;
    private bool _disposed;
    private long _position;
    private long _startSample;
    private long _playedAtStart;
    private long _prefetchAnchor = long.MinValue;
    private long _lastShown = long.MinValue;
    private long _lastDropped = long.MinValue;

    // 前に取り出したフレームが間に合わなかった（描くのが遅れている）
    private bool _behind;
    private long _shown;
    private long _dropped;

    // 混ぜた音のブロックごとの大きさ（レベルメーター用）。鳴る位置で引けるよう、ブロックの先頭のサンプルと組にして覚えておく
    private readonly (long Start, StereoLevel Level)[] _levels = new (long, StereoLevel)[64];
    private int _levelCount;
    private long _mixedSample;

    public PlaybackController(Scene scene, IAudioSource audio, FrameScheduler frames, PlaybackOptions? options = null)
    {
        _frames = frames;
        _options = options ?? new PlaybackOptions();
        _plan = AudioEvaluator.Build(scene);
        _mixer = new AudioMixer(_plan, audio);
        if (!_options.DisableAudioDevice)
        {
            try
            {
                var bufferFrames = Math.Max(BlockFrames * 2, _plan.SampleRate * _options.BufferMilliseconds / 1000);
                _output = AudioOutput.Open(_plan.SampleRate, bufferFrames, _options.NullAudioDevice, _options.OutputDevice);
            }
            catch (AudioDeviceException ex)
            {
                AudioError = ex.Message;
            }
        }

        _thread = new Thread(Work) { IsBackground = true, Name = "再生の音" };
        _thread.Start();
    }

    /// <summary>再生が最後まで行って止まった（再生のスレッドから呼ばれる）</summary>
    public event EventHandler? Ended;

    /// <summary>音のデバイスを使っているか（使えないときは映像だけ再生する）</summary>
    public bool HasAudioOutput => _output is not null;

    /// <summary>音のデバイスを開けなかった理由</summary>
    public string? AudioError { get; }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _playing;
            }
        }
    }

    /// <summary>今鳴っている位置（タイムラインのサンプル）</summary>
    public long CurrentSample
    {
        get
        {
            lock (_gate)
            {
                return Clock();
            }
        }
    }

    /// <summary>今表示するフレーム</summary>
    public long CurrentFrame => TimeMath.SampleToFrame(CurrentSample, _plan.Fps, _plan.SampleRate);

    /// <summary>今鳴っている音の左右の大きさ（止まっているとき・音のデバイスがないときは無音）</summary>
    public StereoLevel Level
    {
        get
        {
            lock (_gate)
            {
                if (!_playing || _output is null)
                {
                    return StereoLevel.Silent;
                }

                var clock = Clock();
                for (var i = 0; i < Math.Min(_levelCount, _levels.Length); i++)
                {
                    var (start, level) = _levels[i];
                    if (clock >= start && clock < start + BlockFrames)
                    {
                        return level;
                    }
                }

                return StereoLevel.Silent;
            }
        }
    }

    public PlaybackStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new PlaybackStatistics(_shown, _dropped, _output?.UnderrunFrames ?? 0);
            }
        }
    }

    /// <summary>今の位置から再生する。最後まで行っていたら、先頭から再生する</summary>
    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playing)
            {
                return;
            }

            if (_position >= _plan.Length)
            {
                _position = 0;
            }

            Restart(_position);
            _output?.Start();
            _playing = true;
            _running.Set();
        }
    }

    /// <summary>止める（位置はそのまま）</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing)
            {
                return;
            }

            _position = Clock();
            _playing = false;
            _running.Reset();
            _stopwatch.Reset();
            _output?.Stop();
            _output?.Clear();
            _frames.CancelPrefetch();
        }
    }

    /// <summary>frame の位置へ移る。再生中なら、そこから続けて再生する</summary>
    public void Seek(long frame)
    {
        var sample = TimeMath.FrameToSample(Math.Max(0, frame), _plan.Fps, _plan.SampleRate);
        lock (_gate)
        {
            _position = sample;
            if (_playing)
            {
                _output?.Clear();
                Restart(sample);
            }
        }
    }

    /// <summary>編集した後のシーンにする（すでに混ぜてバッファに入っている音は、そのまま鳴る）</summary>
    public void SetScene(Scene scene)
    {
        var plan = AudioEvaluator.Build(scene);
        lock (_gate)
        {
            _plan = plan;
            _mixer.SetPlan(plan);
        }
    }

    /// <summary>
    /// 今の位置のフレームを取り出す（画面の更新のたびに呼ぶ）。描き終わっていなければ false を返す
    /// （前のフレームを表示したままにする）。再生中は、この先のフレームを先に描かせておく
    /// </summary>
    public bool TryGetFrame(out RenderedFrame frame)
    {
        long current;
        bool playing;
        lock (_gate)
        {
            current = TimeMath.SampleToFrame(Clock(), _plan.Fps, _plan.SampleRate);
            playing = _playing;
            if (playing && (current < _prefetchAnchor || current >= _prefetchAnchor + _options.PrefetchFrames / 2))
            {
                // 遅れているときは、描き終わるころの位置から先読みする（今のフレームから描くと、描き終わったときにはもう過ぎていて、
                // 先読みし直すたびに同じことを繰り返し、追いつけなくなる）
                _prefetchAnchor = current;
                _frames.Prefetch(_behind ? current + _options.PrefetchFrames / 4 : current, _options.PrefetchFrames);
            }
        }

        if (_frames.TryGetCached(current, out frame))
        {
            lock (_gate)
            {
                _behind = false;
                if (current != _lastShown)
                {
                    _lastShown = current;
                    _shown++;
                }
            }

            return true;
        }

        if (playing)
        {
            lock (_gate)
            {
                _behind = true;
                if (current != _lastDropped)
                {
                    _lastDropped = current;
                    _dropped++;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _playing = false;
            _running.Set();
        }

        _thread.Join();
        _output?.Dispose();
        _running.Dispose();
    }

    // 時計：デバイスが鳴らした量から、デバイスの中にあってまだ鳴っていない分を引く。デバイスがなければストップウォッチ
    private long Clock()
    {
        if (!_playing)
        {
            return _position;
        }

        long elapsed;
        if (_output is not null)
        {
            elapsed = Math.Max(0, _output.FramesPlayed - _playedAtStart - _output.LatencyFrames);
        }
        else
        {
            elapsed = (long)(_stopwatch.Elapsed.TotalSeconds * _plan.SampleRate);
        }

        return Math.Min(_startSample + elapsed, Math.Max(_startSample, _plan.Length));
    }

    // sample から再生し直す。音は、バッファの分だけ先に混ぜておく（始めてすぐに途切れないように）
    private void Restart(long sample)
    {
        _startSample = sample;
        _mixer.Seek(sample);
        _mixedSample = sample;
        _levelCount = 0;
        _prefetchAnchor = long.MinValue;
        _behind = false;
        if (_output is not null)
        {
            _playedAtStart = _output.FramesPlayed;
            Fill();
        }

        _stopwatch.Restart();
    }

    // リングバッファに入るだけ混ぜて書き込む
    private void Fill()
    {
        while (_output!.WritableFrames >= BlockFrames)
        {
            _mixer.Render(_block);
            _output.Write(_block);
            _levels[_levelCount++ % _levels.Length] = (_mixedSample, StereoLevel.Measure(_block));
            _mixedSample += BlockFrames;
        }
    }

    private void Work()
    {
        while (true)
        {
            _running.Wait();
            var ended = false;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_playing)
                {
                    if (_output is not null)
                    {
                        Fill();
                    }

                    if (Clock() >= _plan.Length)
                    {
                        _position = _plan.Length;
                        _playing = false;
                        _running.Reset();
                        _output?.Stop();
                        _output?.Clear();
                        _frames.CancelPrefetch();
                        ended = true;
                    }
                }
            }

            if (ended)
            {
                Ended?.Invoke(this, EventArgs.Empty);
            }

            Thread.Sleep(3);
        }
    }
}
