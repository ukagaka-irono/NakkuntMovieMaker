using System.Collections.Concurrent;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;

namespace NakkuntMovieMaker.Engine;

/// <summary>
/// プロジェクトの素材を、描画に渡す。画像は縮小版キャッシュ（MediaCache）とメモリ上のキャッシュから、
/// 動画は素材ごとのデコーダーから取り出す。描くのに必要な大きさは 1・1/2・1/4・1/8 の段階に丸め、
/// 段階ごとに縮小した画像を用意する（少し大きさが変わるたびに作り直さないように）。
/// 複数のスレッドから同時に使ってよい（同じ動画は、デコーダーを順番に使う）
/// </summary>
public sealed class ProjectMediaSource : IMediaSource, IAudioSource, IDisposable
{
    private static readonly double[] Steps = [1, 0.5, 0.25, 0.125];

    // メモリ上に置く画像の合計の上限（バイト）。超えたら捨てて、使うものだけ作り直す
    private const long ImageMemoryLimit = 512L * 1024 * 1024;

    private Project _project;
    private readonly string _projectFolder;
    private readonly MediaCache _cache;
    private readonly ConversionQuality _quality;
    private readonly ConcurrentDictionary<string, MediaInfo?> _info = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(AssetId Asset, int Step), SourceImage?> _images = new();
    private readonly ConcurrentDictionary<(string Path, int Step), SourceImage?> _files = new();
    private readonly ConcurrentDictionary<(AssetId Asset, int Step), DecoderSlot?> _decoders = new();
    private readonly ConcurrentDictionary<AssetId, Lazy<PcmAudio?>> _audio = new();
    private readonly ConcurrentDictionary<string, Lazy<PcmAudio?>> _voices = new(StringComparer.Ordinal);
    private long _imageBytes;

    /// <param name="quality">動画の RGB への変換の質（プレビューは速さ、書き出しは質を優先する）</param>
    public ProjectMediaSource(Project project, string projectFolder, MediaCache cache, ConversionQuality quality = ConversionQuality.Fast)
    {
        _project = project;
        _projectFolder = projectFolder;
        _cache = cache;
        _quality = quality;
    }

    public SourceImage? GetImage(AssetId asset, double scale)
    {
        var step = StepFor(scale);
        if (_images.TryGetValue((asset, step), out var cached))
        {
            return cached;
        }

        var image = LoadImage(asset, step);
        if (image is not null && Interlocked.Add(ref _imageBytes, image.Image.Pixels.Length) > ImageMemoryLimit)
        {
            _images.Clear();
            Interlocked.Exchange(ref _imageBytes, image.Image.Pixels.Length);
        }

        return _images.GetOrAdd((asset, step), image);
    }

    /// <summary>ファイルの場所で指定した画像（立ち絵のパーツなど）。素材と同じく、段階ごとに縮小して覚えておく</summary>
    public SourceImage? GetImageFile(string path, double scale)
    {
        var step = StepFor(scale);
        if (_files.TryGetValue((path, step), out var cached))
        {
            return cached;
        }

        var image = LoadImageFile(path, step);
        if (image is not null && Interlocked.Add(ref _imageBytes, image.Image.Pixels.Length) > ImageMemoryLimit)
        {
            _images.Clear();
            _files.Clear();
            Interlocked.Exchange(ref _imageBytes, image.Image.Pixels.Length);
        }

        return _files.GetOrAdd((path, step), image);
    }

    public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale)
    {
        var slot = _decoders.GetOrAdd((asset, StepFor(scale)), key => OpenDecoder(key.Asset, key.Step));
        if (slot is null)
        {
            return null;
        }

        lock (slot.Gate)
        {
            try
            {
                return new SourceImage(slot.Decoder.GetFrame(time).Image, slot.Decoder.SourceWidth, slot.Decoder.SourceHeight);
            }
            catch (Exception ex) when (ex is MediaException or ObjectDisposedException)
            {
                // 読めない動画や、素材の差し替え（Update）で閉じたデコーダーは、描かずに飛ばす
                return null;
            }
        }
    }

    /// <summary>
    /// 編集した後のプロジェクトにする。場所が変わった・なくなった素材は、デコーダーと画像を捨てて読み直す
    /// </summary>
    public void Update(Project project)
    {
        var previous = Interlocked.Exchange(ref _project, project);
        foreach (var (id, asset) in previous.Assets)
        {
            if (project.Assets.TryGetValue(id, out var current) && current.Path == asset.Path)
            {
                continue;
            }

            foreach (var key in _images.Keys.Where(key => key.Asset == id))
            {
                _images.TryRemove(key, out _);
            }

            if (_audio.TryRemove(id, out var audio) && audio.IsValueCreated)
            {
                audio.Value?.Dispose();
            }

            foreach (var key in _decoders.Keys.Where(key => key.Asset == id))
            {
                if (_decoders.TryRemove(key, out var slot) && slot is not null)
                {
                    lock (slot.Gate)
                    {
                        slot.Decoder.Dispose();
                    }
                }
            }
        }

        // 素材がなかった ID に素材が加わったとき（見つからなかった記録を消す）
        foreach (var id in project.Assets.Keys.Where(id => !previous.Assets.ContainsKey(id)))
        {
            foreach (var key in _images.Keys.Where(key => key.Asset == id))
            {
                _images.TryRemove(key, out _);
            }

            foreach (var key in _decoders.Keys.Where(key => key.Asset == id))
            {
                _decoders.TryRemove(key, out _);
            }

            _audio.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 素材の音声を、48kHz の PCM キャッシュから読めるようにする（キャッシュがなければ作るので、初回は時間がかかる）
    /// </summary>
    public IAudioStream? OpenAudio(AssetId asset)
    {
        var audio = _audio.GetOrAdd(asset, id => new Lazy<PcmAudio?>(() => OpenPcm(PathOf(id)))).Value;
        return audio is null ? null : new PcmAudioStream(audio);
    }

    /// <summary>合成した声のキャッシュ（プロジェクトの voices/&lt;キー&gt;.wav）を、48kHz の PCM キャッシュから読めるようにする</summary>
    public IAudioStream? OpenVoice(string key)
    {
        var audio = _voices.GetOrAdd(key, name => new Lazy<PcmAudio?>(() => OpenPcm(Path.Combine(_projectFolder, "voices", name + ".wav")))).Value;
        return audio is null ? null : new PcmAudioStream(audio);
    }

    private PcmAudio? OpenPcm(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return _cache.OpenAudio(path);
        }
        catch (MediaException)
        {
            return null;
        }
    }

    public string ResolvePath(string reference) =>
        Path.IsPathRooted(reference) ? reference : Path.GetFullPath(Path.Combine(_projectFolder, reference));

    public void Dispose()
    {
        foreach (var slot in _decoders.Values)
        {
            slot?.Decoder.Dispose();
        }

        _decoders.Clear();
        _images.Clear();
        _files.Clear();
        foreach (var audio in _audio.Values.Concat(_voices.Values).Where(audio => audio.IsValueCreated))
        {
            audio.Value?.Dispose();
        }

        _audio.Clear();
        _voices.Clear();
    }

    // 必要な倍率以上で、いちばん小さい段階
    private static int StepFor(double scale)
    {
        for (var i = Steps.Length - 1; i > 0; i--)
        {
            if (Steps[i] >= scale)
            {
                return i;
            }
        }

        return 0;
    }

    private string? PathOf(AssetId asset) =>
        Volatile.Read(ref _project).Assets.TryGetValue(asset, out var entry) ? AssetLinks.Resolve(entry, _projectFolder) : null;

    private MediaInfo? Info(string path) =>
        _info.GetOrAdd(path, p =>
        {
            try
            {
                return File.Exists(p) ? MediaProbe.Probe(p) : null;
            }
            catch (MediaException)
            {
                return null;
            }
        });

    private SourceImage? LoadImage(AssetId asset, int step) => PathOf(asset) is { } path ? LoadImageFile(path, step) : null;

    private SourceImage? LoadImageFile(string path, int step)
    {
        if (Info(path)?.Video is not { } video)
        {
            return null;
        }

        try
        {
            var image = _cache.GetImage(path, MaxSize(video.Width, step), MaxSize(video.Height, step));
            return new SourceImage(image, video.Width, video.Height);
        }
        catch (MediaException)
        {
            return null;
        }
    }

    private DecoderSlot? OpenDecoder(AssetId asset, int step)
    {
        if (PathOf(asset) is not { } path || Info(path)?.Video is not { } video)
        {
            return null;
        }

        try
        {
            var options = new VideoDecoderOptions(MaxSize(video.Width, step), MaxSize(video.Height, step), Quality: _quality);
            return new DecoderSlot(VideoDecoder.Open(path, options));
        }
        catch (MediaException)
        {
            return null;
        }
    }

    private static int MaxSize(int size, int step) => Math.Max(1, (int)Math.Ceiling(size * Steps[step]));

    private sealed record DecoderSlot(VideoDecoder Decoder)
    {
        public Lock Gate { get; } = new();
    }
}
