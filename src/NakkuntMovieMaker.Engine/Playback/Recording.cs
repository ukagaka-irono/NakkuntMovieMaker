using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Engine.Playback;

/// <summary>録った音を、タイムラインに置く</summary>
public static class Recording
{
    /// <summary>
    /// 録った音をプロジェクトの recordings フォルダに置くときの場所（日時から名前を付ける。同じ名前があれば番号を付ける）
    /// </summary>
    public static string NewPath(string projectFolder, DateTimeOffset now)
    {
        var folder = Path.Combine(projectFolder, "recordings");
        var name = now.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, name + ".wav");
        for (var number = 2; File.Exists(path); number++)
        {
            path = Path.Combine(folder, $"{name}_{number}.wav");
        }

        return path;
    }

    /// <summary>
    /// 録った音を置くアイテムの位置。アイテムはフレーム単位でしか置けないので、置く位置（サンプル）以降で最初のフレームから始め、
    /// その手前のずれは素材の先頭を削って合わせる（音はサンプル単位で正しい位置に鳴る）
    /// </summary>
    public static (long Start, long Length, Rational SourceOffset) Timing(RecordingResult result, SceneFormat format)
    {
        var placement = Math.Max(0, result.PlacementSample);
        var start = TimeMath.SampleToFrame(placement, format.Fps, format.SampleRate);
        if (TimeMath.FrameToSample(start, format.Fps, format.SampleRate) < placement)
        {
            start++;
        }

        var startSample = TimeMath.FrameToSample(start, format.Fps, format.SampleRate);
        var skipped = startSample - result.PlacementSample;
        var endSample = result.PlacementSample + result.Frames;
        var end = TimeMath.SampleToFrame(Math.Max(endSample - 1, startSample), format.Fps, format.SampleRate) + 1;
        return (start, Math.Max(1, end - start), new Rational(Math.Max(0, skipped), result.SampleRate));
    }

    /// <summary>録った音を素材として加え、layer に音声アイテムとして置く操作。置いたアイテムの ID も返す</summary>
    public static (IOperation Operation, ItemId Item) AddToTimeline(
        Project project, SceneId sceneId, LayerId layer, RecordingResult result, string projectFolder)
    {
        var scene = project.GetScene(sceneId);
        var asset = new Asset(AssetId.New(), AssetKind.Audio, AssetLinks.ToStoredPath(result.Path, projectFolder));
        var (start, length, offset) = Timing(result, scene.Format);
        var item = new AudioItem(ItemId.New(), layer, start, length, asset.Id)
        {
            Playback = MediaPlayback.Normal with { SourceOffset = offset },
        };

        var withAsset = project with { Assets = project.Assets.SetItem(asset.Id, asset) };
        var operation = Operations.Combine("録音", [new SetAsset(asset.Id, asset), EditCommands.AddItem(withAsset, sceneId, item)]);
        return (operation, item.Id);
    }
}

/// <summary>
/// 入出力の遅れの補正値を測る。出力でクリックを鳴らしながら、再生に合わせた録音と同じ方法で録り、
/// クリックが置かれるはずの位置と、録れた位置の差を求める（スピーカーとマイク、またはループバックの配線が必要）
/// </summary>
public static class LatencyCalibration
{
    private const int SampleRate = 48000;

    /// <summary>補正値（フレーム。Recorder の extraLatency に渡す）。クリックが録れなかったら null</summary>
    /// <exception cref="AudioDeviceException">デバイスを開けなかった</exception>
    public static int? Measure(string? outputDevice = null, string? inputDevice = null, bool nullDevices = false, CancellationToken cancellationToken = default)
    {
        const int interval = SampleRate / 4;
        const int count = 8;
        var clicks = LatencyMeasurement.CreateClicks(count, interval);
        var stereo = clicks.SelectMany(value => new[] { value, value }).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"nmm-latency-{Guid.NewGuid():N}.wav");

        using var output = AudioOutput.Open(SampleRate, SampleRate / 5, nullDevices, outputDevice);
        using var input = AudioInput.Open(SampleRate, 1, SampleRate * 2, nullDevices, inputDevice);
        try
        {
            // 出力を始めたときを 0 とする時計（鳴らしたフレーム − 出力の遅れ）
            long Clock() => Math.Max(0, output.FramesPlayed - output.LatencyFrames);

            var written = 0;
            void Feed()
            {
                while (output.WritableFrames >= 512 && written < stereo.Length / 2 + SampleRate)
                {
                    var frames = Math.Min(512, stereo.Length / 2 - written);
                    var block = frames > 0 ? stereo.AsSpan(written * 2, frames * 2) : new float[1024];
                    written += output.Write(block);
                }
            }

            Feed();
            output.Start();
            using var recorder = Recorder.Start(input, path, 0, Clock);
            var deadline = Environment.TickCount64 + (count * interval + SampleRate) * 1000L / SampleRate;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Feed();
                Thread.Sleep(5);
            }

            var result = recorder.Stop();
            output.Stop();
            var recorded = ReadMono(path);

            // 録れた音の中でクリックが現れた位置から、タイムラインでの位置を求め、本来の位置（0）との差を補正値にする
            var skip = (int)Math.Max(0, -result.PlacementSample);
            if (LatencyMeasurement.FindDelay(clicks, recorded.AsSpan(Math.Min(skip, recorded.Length)), SampleRate) is not { } delay)
            {
                return null;
            }

            return (int)(result.PlacementSample + skip + delay);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Recorder が書いた float32 の WAV（ヘッダー 44 バイト）を読む
    private static float[] ReadMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(44)).ToArray();
    }
}
