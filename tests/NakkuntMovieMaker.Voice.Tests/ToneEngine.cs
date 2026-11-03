namespace NakkuntMovieMaker.Voice.Tests;

/// <summary>
/// テスト用の音声合成エンジン。読みの 1 文字を 0.1 秒の音（24kHz）にする。
/// 呼ばれた回数・スレッド・同時に動いた数を記録する
/// </summary>
internal sealed class ToneEngine(string id = "tone", string version = "1") : IVoiceEngine
{
    public const int Rate = 24000;

    private int _running;

    public string Id => id;

    public string Name => $"テスト用（{id}）";

    public string Version { get; set; } = version;

    public VoiceEngineStatus Status { get; set; } = VoiceEngineStatus.Ready;

    public IReadOnlyList<VoiceInfo> Voices { get; } = [new("a", "声 A"), new("b", "声 B")];

    public int Calls;

    public int MaxConcurrency;

    public HashSet<string?> Threads { get; } = [];

    public List<VoiceRequest> Requests { get; } = [];

    public TimeSpan Delay { get; set; }

    public bool Fail { get; set; }

    public string? MakeReading(string text) => $"ヨミ:{text}";

    public VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
    {
        var running = Interlocked.Increment(ref _running);
        try
        {
            lock (Threads)
            {
                MaxConcurrency = Math.Max(MaxConcurrency, running);
                Threads.Add(Thread.CurrentThread.Name);
                Requests.Add(request);
                Calls++;
            }

            Thread.Sleep(Delay);
            if (Fail)
            {
                throw new InvalidOperationException("合成できませんでした");
            }

            var length = (request.Reading ?? request.Text).Length * Rate / 10;
            var samples = new float[length];
            for (var i = 0; i < length; i++)
            {
                samples[i] = (float)(0.5 * request.Params.Volume * Math.Sin(2 * Math.PI * 220 * i / Rate));
            }

            return new VoiceAudio(samples, Rate);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public void Dispose()
    {
    }
}
