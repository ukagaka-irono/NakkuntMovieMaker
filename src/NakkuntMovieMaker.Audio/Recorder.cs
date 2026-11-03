namespace NakkuntMovieMaker.Audio;

/// <summary>入力のレベル（dBFS）。Peak は最大の振幅、Rms は平均の大きさ。無音なら Loudness.Silence</summary>
public readonly record struct AudioLevel(double PeakDb, double RmsDb)
{
    public static readonly AudioLevel Silent = new(Loudness.Silence, Loudness.Silence);
}

/// <summary>
/// 録音の結果。PlacementSample は、録った音の先頭を置くタイムラインの位置（サンプル）。
/// 再生に合わせて録ったときは、入出力の遅れを補正した位置になる
/// </summary>
public sealed record RecordingResult(string Path, long Frames, int SampleRate, int Channels, long PlacementSample, long OverrunFrames);

/// <summary>
/// 入力の音を WAV（float32）に書きながら、レベルを測る。専用のスレッドで入力を読み続ける。
/// <para>
/// 置く位置：clock（今鳴っている位置を返す関数）を渡さなければ、startSample に置く。
/// 渡したとき（再生しながらの録音）は、読み出すたびに「今鳴っている位置 − 入力の遅れ − ここまで録れた量」から
/// 先頭の位置を見積もり、その中央値を使う（読み出しの時刻のぶれをならす）。
/// 再生が始まったばかりで時計が動いていない間の見積もりは使わない
/// </para>
/// </summary>
public sealed class Recorder : IDisposable
{
    // 位置の見積もりをいくつ集めるか
    private const int PlacementSamples = 64;

    // レベルを測る長さ（ミリ秒）
    private const int LevelWindowMilliseconds = 50;

    private readonly IAudioInput _input;
    private readonly WavWriter _writer;
    private readonly string _path;
    private readonly long _startSample;
    private readonly Func<long>? _clock;
    private readonly int _extraLatency;
    private readonly Thread _thread;
    private readonly List<long> _placements = [];
    private readonly float[] _buffer;
    private readonly Lock _gate = new();
    private volatile bool _stopping;
    private long _frames;
    private long? _firstClock;
    private AudioLevel _level = AudioLevel.Silent;
    private double _windowPeak;
    private double _windowSquares;
    private int _windowFrames;
    private Exception? _error;

    private Recorder(IAudioInput input, string path, long startSample, Func<long>? clock, int extraLatency)
    {
        _input = input;
        _path = path;
        _startSample = startSample;
        _clock = clock;
        _extraLatency = extraLatency;
        _buffer = new float[Math.Max(1024, input.SampleRate / 20) * input.Channels];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new WavWriter(path, input.Channels, input.SampleRate, WavSampleFormat.Float32);
        _thread = new Thread(Work) { IsBackground = true, Name = "録音" };
    }

    /// <summary>今のレベル（最後の 50ms）</summary>
    public AudioLevel Level
    {
        get
        {
            lock (_gate)
            {
                return _level;
            }
        }
    }

    /// <summary>ここまでに録れたフレームの数</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>
    /// 録音を始める。path に WAV を書き、startSample に置く。clock を渡すと、再生に合わせて録る（置く位置を見積もる）。
    /// extraLatency は、測った入出力の遅れの補正（フレーム。正の値なら、録った音を前にずらす）
    /// </summary>
    public static Recorder Start(IAudioInput input, string path, long startSample, Func<long>? clock = null, int extraLatency = 0)
    {
        var recorder = new Recorder(input, path, startSample, clock, extraLatency);
        input.Start();
        recorder._thread.Start();
        return recorder;
    }

    /// <summary>録音を止め、残りを書いて結果を返す</summary>
    /// <exception cref="IOException">WAV を書けなかった</exception>
    public RecordingResult Stop()
    {
        if (!_stopping)
        {
            _stopping = true;
            _thread.Join();
            _input.Stop();
            Drain();
            _writer.Dispose();
        }

        if (_error is not null)
        {
            throw new IOException($"録音を書き込めませんでした: {_path}", _error);
        }

        return new RecordingResult(_path, _frames, _input.SampleRate, _input.Channels, Placement() - _extraLatency, _input.OverrunFrames);
    }

    public void Dispose()
    {
        if (!_stopping)
        {
            try
            {
                Stop();
            }
            catch (IOException)
            {
            }
        }
    }

    private void Work()
    {
        while (!_stopping)
        {
            try
            {
                Drain();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 書き込みや入力デバイスの失敗でアプリを止めない（Stop で知らせる）
                _error = ex;
                return;
            }

            Thread.Sleep(5);
        }
    }

    // 読めるだけ読んで書き、レベルと位置の見積もりを更新する
    private void Drain()
    {
        var channels = _input.Channels;
        while (true)
        {
            var count = _input.Read(_buffer);
            if (count == 0)
            {
                return;
            }

            var samples = _buffer.AsSpan(0, count * channels);
            _writer.Write(samples);
            var total = Interlocked.Add(ref _frames, count);
            Measure(samples, channels);
            Estimate(total);
        }
    }

    private void Estimate(long total)
    {
        if (_clock is null || _placements.Count >= PlacementSamples)
        {
            return;
        }

        var now = _clock();
        _firstClock ??= now;
        if (now == _firstClock)
        {
            return;
        }

        // 最後に届いたフレームは、入力の遅れに加えて、平均すると 1 回分の半分だけ前に入ってきたもの
        _placements.Add(now - _input.LatencyFrames - _input.PeriodFrames / 2 - total);
    }

    private long Placement()
    {
        if (_placements.Count == 0)
        {
            return _startSample;
        }

        var sorted = _placements.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private void Measure(ReadOnlySpan<float> samples, int channels)
    {
        var window = _input.SampleRate * LevelWindowMilliseconds / 1000;
        for (var i = 0; i < samples.Length; i += channels)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var value = Math.Abs(samples[i + channel]);
                _windowPeak = Math.Max(_windowPeak, value);
                _windowSquares += value * value / channels;
            }

            if (++_windowFrames >= window)
            {
                var level = new AudioLevel(
                    _windowPeak > 0 ? 20 * Math.Log10(_windowPeak) : Loudness.Silence,
                    _windowSquares > 0 ? 10 * Math.Log10(_windowSquares / _windowFrames) : Loudness.Silence);
                lock (_gate)
                {
                    _level = level;
                }

                _windowPeak = 0;
                _windowSquares = 0;
                _windowFrames = 0;
            }
        }
    }
}

/// <summary>
/// 入出力の遅れの測定。出力に決まった音（クリック）を鳴らして入力で録り、録れた音の中でクリックがどれだけ遅れて現れたかを、
/// 相互相関で求める（スピーカーとマイク、またはループバックの配線が必要）
/// </summary>
public static class LatencyMeasurement
{
    /// <summary>
    /// recorded の中で reference が最もよく一致する遅れ（サンプル）を、0〜maxDelay の範囲で探す。
    /// 一致の度合いが低い（音が届いていない）ときは null
    /// </summary>
    public static int? FindDelay(ReadOnlySpan<float> reference, ReadOnlySpan<float> recorded, int maxDelay)
    {
        double referenceEnergy = 0;
        foreach (var value in reference)
        {
            referenceEnergy += value * value;
        }

        if (referenceEnergy <= 0)
        {
            return null;
        }

        var best = -1;
        var bestScore = 0.0;
        for (var delay = 0; delay <= maxDelay && delay + reference.Length <= recorded.Length; delay++)
        {
            double dot = 0;
            double energy = 0;
            for (var i = 0; i < reference.Length; i++)
            {
                var value = recorded[delay + i];
                dot += reference[i] * value;
                energy += value * value;
            }

            // 正規化した相関（-1〜1）で比べる
            var score = energy > 0 ? dot / Math.Sqrt(referenceEnergy * energy) : 0;
            if (score > bestScore)
            {
                bestScore = score;
                best = delay;
            }
        }

        return best >= 0 && bestScore >= 0.5 ? best : null;
    }

    /// <summary>測定に使うクリック（短いノイズのバースト）を、interval サンプルおきに count 回並べたモノラルの音</summary>
    public static float[] CreateClicks(int count, int interval, int seed = 1)
    {
        var random = new Random(seed);
        var signal = new float[count * interval];
        for (var click = 0; click < count; click++)
        {
            for (var i = 0; i < 96; i++)
            {
                signal[click * interval + i] = (float)((random.NextDouble() * 2 - 1) * 0.5 * (1 - i / 96.0));
            }
        }

        return signal;
    }
}
