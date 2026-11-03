using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.Audio.Tests;

public sealed class RecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));

    // 時計と入力が共通に使う時刻
    private readonly Stopwatch _time = Stopwatch.StartNew();

    public RecorderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static double Tone(long frame) => 0.5 * Math.Sin(2 * Math.PI * 440 * frame / 48000.0);

    private static float[] ReadWav(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path).AsSpan(44)).ToArray();

    [Fact]
    public void WritesTheInputToWav()
    {
        using var input = new SimulatedInput(_time, Tone, latencyFrames: 0, periodFrames: 480);
        var recorder = Recorder.Start(input, Path.Combine(_folder, "take.wav"), startSample: 96000);
        Thread.Sleep(500);
        var result = recorder.Stop();

        Assert.InRange(result.Frames, 24000 - 4800, 24000 + 4800);
        Assert.Equal((48000, 1, 96000L, 0L), (result.SampleRate, result.Channels, result.PlacementSample, result.OverrunFrames));
        var samples = ReadWav(result.Path);
        Assert.Equal(result.Frames, samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal((float)Tone(i), samples[i]);
        }
    }

    [Fact]
    public void MeasuresTheInputLevel()
    {
        using var input = new SimulatedInput(_time, Tone, latencyFrames: 0, periodFrames: 480);
        using var recorder = Recorder.Start(input, Path.Combine(_folder, "level.wav"), 0);
        Thread.Sleep(300);

        var level = recorder.Level;

        // 振幅 0.5 の正弦波：山は −6.02dB、平均の大きさ（RMS）は −9.03dB
        Assert.Equal(-6.02, level.PeakDb, 0.05);
        Assert.Equal(-9.03, level.RmsDb, 0.1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2400, 480)] // 入力の遅れ 50ms、10ms ごとに届く
    [InlineData(4800, 1024)]
    public void PlacesTheRecordingWhereThePlaybackWas(int latency, int period)
    {
        // 時計（再生の位置）は、録音を始める 0.25 秒前に 1 秒（48000）から動き始めている。
        // 録音の先頭は、入力が始まったときに鳴っていた位置（約 1.25 秒）に置かれるはず
        var clockStart = _time.Elapsed.TotalSeconds;
        long Clock() => 48000 + (long)((_time.Elapsed.TotalSeconds - clockStart) * 48000);
        Thread.Sleep(250);

        using var input = new SimulatedInput(_time, Tone, latency, period);
        var recorder = Recorder.Start(input, Path.Combine(_folder, $"along-{latency}.wav"), 0, Clock);
        Thread.Sleep(800);
        var result = recorder.Stop();
        var expected = 48000 + (long)((input.StartedAt - clockStart) * 48000);

        // 誤差は 1 フレーム（30fps で 1600 サンプル）以内
        Assert.InRange(result.PlacementSample, expected - 800, expected + 800);
    }

    [Fact]
    public void IgnoresTheClockUntilItStartsMoving()
    {
        // 再生を始めたばかりで、時計が出力の遅れの間（0.2 秒）0 のまま止まっている
        var playStart = _time.Elapsed.TotalSeconds;
        long Clock() => (long)(Math.Max(0, _time.Elapsed.TotalSeconds - playStart - 0.2) * 48000);

        using var input = new SimulatedInput(_time, Tone, latencyFrames: 480, periodFrames: 480);
        var recorder = Recorder.Start(input, Path.Combine(_folder, "stall.wav"), 0, Clock);
        Thread.Sleep(800);
        var result = recorder.Stop();

        // 入力が始まったときは、時計が動き出す約 0.2 秒前（約 −9600 の位置）
        var expected = (long)((input.StartedAt - playStart - 0.2) * 48000);
        Assert.InRange(result.PlacementSample, expected - 800, expected + 800);
    }

    [Fact]
    public void ExtraLatencyMovesTheRecordingEarlier()
    {
        using var input = new SimulatedInput(_time, Tone, latencyFrames: 0, periodFrames: 480);
        var recorder = Recorder.Start(input, Path.Combine(_folder, "extra.wav"), 10000, extraLatency: 1200);
        Thread.Sleep(100);

        Assert.Equal(8800, recorder.Stop().PlacementSample);
    }

    /// <summary>
    /// 決まった信号を実時間で入力する（テスト用）。latencyFrames だけ遅れて、periodFrames ずつまとめて届く。
    /// 時刻は、テストの時計と同じ time で測る
    /// </summary>
    private sealed class SimulatedInput(Stopwatch time, Func<long, double> signal, int latencyFrames, int periodFrames) : IAudioInput
    {
        private long _read;

        /// <summary>入力を始めた時刻（time の秒）</summary>
        public double StartedAt { get; private set; } = double.NaN;

        public int SampleRate => 48000;

        public int Channels => 1;

        public int LatencyFrames => latencyFrames;

        public int PeriodFrames => periodFrames;

        public long OverrunFrames => 0;

        public void Start() => StartedAt = time.Elapsed.TotalSeconds;

        public void Stop()
        {
        }

        public int Read(Span<float> destination)
        {
            // 遅れの分を引いて、届いたまとまりの分だけ読める
            var arrived = (long)((time.Elapsed.TotalSeconds - StartedAt) * SampleRate) - latencyFrames;
            var available = Math.Max(0, arrived / periodFrames * periodFrames) - _read;
            var count = (int)Math.Clamp(available, 0, destination.Length);
            for (var i = 0; i < count; i++)
            {
                destination[i] = (float)signal(_read + i);
            }

            _read += count;
            return count;
        }

        public void Dispose()
        {
        }
    }
}
