namespace NakkuntMovieMaker.Audio;

/// <summary>
/// アイテムが使う素材の範囲を、ステレオで先頭から読むためのもの。Reverse なら範囲の後ろから読む。
/// 範囲の外も素材の続き（素材の外なら無音）を読むので、境目で途切れない
/// </summary>
internal sealed class ClipSourceView(IAudioStream stream, long start, long length, bool reverse)
{
    private float[] _scratch = [];

    public int SampleRate => stream.SampleRate;

    /// <summary>位置 position（範囲の先頭からのフレーム）から、destination の長さぶんのステレオのサンプルを読む</summary>
    public void ReadStereo(long position, Span<float> destination)
    {
        var frames = destination.Length / 2;
        var channels = stream.Channels;
        var needed = frames * channels;
        if (_scratch.Length < needed)
        {
            _scratch = new float[needed];
        }

        var raw = _scratch.AsSpan(0, needed);

        // 逆向きのときは、読む範囲の最後のフレームから前へ並べる
        var first = reverse ? start + length - position - frames : start + position;
        stream.Read(first, raw);
        for (var i = 0; i < frames; i++)
        {
            var from = (reverse ? frames - 1 - i : i) * channels;
            var left = raw[from];
            var right = channels >= 2 ? raw[from + 1] : left;
            destination[i * 2] = left;
            destination[i * 2 + 1] = right;
        }
    }
}

/// <summary>
/// 音の高さを保ったまま速さを変える（WSOLA：波形の似ている位置を探して重ね合わせる方法）。
/// 出力を先頭から順に作り、途中の位置から始めるときは Reset で位置を合わせる。
/// 窓は 32ms（48kHz で 1536 サンプル）、半分ずつ重ね、±8ms の範囲で似ている位置を探す
/// </summary>
internal sealed class TimeStretcher
{
    private const int Window = 1536;
    private const int Hop = Window / 2;
    private const int Tolerance = 384;

    private static readonly float[] Hann = CreateHann();

    private readonly ClipSourceView _source;
    private readonly double _speed;
    private readonly float[] _frame = new float[(Window + 2 * Tolerance) * 2];
    private readonly float[] _natural = new float[Hop * 2];
    private readonly float[] _overlap = new float[Hop * 2];
    private readonly float[] _ready = new float[Hop * 2];
    private long _hop;
    private long _previousInput;
    private int _readyOffset;
    private int _readyCount;

    public TimeStretcher(ClipSourceView source, double speed)
    {
        _source = source;
        _speed = speed;
        Reset(0);
    }

    /// <summary>次に出力するフレーム（範囲の先頭からの位置）</summary>
    public long Position { get; private set; }

    /// <summary>出力の位置 output から作り直す</summary>
    public void Reset(long output)
    {
        // 1 つ手前の窓を作って重ね合わせの残りを用意し、途中から始めても音量が欠けないようにする
        var hop = Math.Max(0, output / Hop - 1);
        _hop = hop;
        Array.Clear(_overlap);
        _previousInput = long.MinValue;
        _readyCount = 0;
        _readyOffset = 0;
        NextHop();
        Position = _hop * Hop - Hop;
        Skip(output - Position);
    }

    /// <summary>続きを destination（ステレオ）に書く</summary>
    public void Read(Span<float> destination)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (_readyCount == 0)
            {
                NextHop();
            }

            var count = Math.Min(_readyCount * 2, destination.Length - written);
            _ready.AsSpan(_readyOffset * 2, count).CopyTo(destination[written..]);
            written += count;
            _readyOffset += count / 2;
            _readyCount -= count / 2;
            Position += count / 2;
        }
    }

    private void Skip(long frames)
    {
        Span<float> discard = stackalloc float[256];
        while (frames > 0)
        {
            var count = (int)Math.Min(frames, discard.Length / 2);
            Read(discard[..(count * 2)]);
            frames -= count;
        }
    }

    // 次の窓を重ね合わせ、Hop フレームぶんの出力を _ready に用意する
    private void NextHop()
    {
        var nominal = (long)Math.Round(_hop * Hop * _speed);
        var input = nominal;
        if (_previousInput != long.MinValue)
        {
            // 前の窓の自然な続き（前の位置 + Hop）と波形が最も似ている位置を、nominal の近くで探す
            _source.ReadStereo(_previousInput + Hop, _natural);
            _source.ReadStereo(nominal - Tolerance, _frame.AsSpan(0, (Hop + 2 * Tolerance) * 2));
            input = nominal - Tolerance + BestOffset();
        }

        _source.ReadStereo(input, _frame.AsSpan(0, Window * 2));
        for (var n = 0; n < Hop; n++)
        {
            _ready[n * 2] = _overlap[n * 2] + _frame[n * 2] * Hann[n];
            _ready[n * 2 + 1] = _overlap[n * 2 + 1] + _frame[n * 2 + 1] * Hann[n];
            _overlap[n * 2] = _frame[(n + Hop) * 2] * Hann[n + Hop];
            _overlap[n * 2 + 1] = _frame[(n + Hop) * 2 + 1] * Hann[n + Hop];
        }

        _previousInput = input;
        _readyOffset = 0;
        _readyCount = Hop;
        _hop++;
    }

    // _frame（nominal − Tolerance から読んだもの）の中で、_natural と最も相関が高いずれ（0〜2×Tolerance）。
    // 計算を減らすため、左右を足した音を 2 サンプルおきに比べる
    private int BestOffset()
    {
        var best = Tolerance;
        var bestScore = double.NegativeInfinity;
        for (var offset = 0; offset <= 2 * Tolerance; offset++)
        {
            double score = 0;
            for (var n = 0; n < Hop; n += 2)
            {
                var a = _frame[(offset + n) * 2] + _frame[(offset + n) * 2 + 1];
                var b = _natural[n * 2] + _natural[n * 2 + 1];
                score += a * b;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }

        return best;
    }

    // 周期的なハン窓（半分ずつ重ねると、足して 1 になる）
    private static float[] CreateHann()
    {
        var window = new float[Window];
        for (var n = 0; n < Window; n++)
        {
            window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Window));
        }

        return window;
    }
}
