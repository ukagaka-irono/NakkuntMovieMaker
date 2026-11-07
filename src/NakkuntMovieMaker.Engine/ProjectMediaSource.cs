using System.Collections.Concurrent;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.ColorManagement;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Color;

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
    private readonly ConcurrentDictionary<AssetId, ColorLut?> _inputLuts = new();
    private readonly ConcurrentDictionary<AssetId, string> _inputErrors = new();
    private readonly ConcurrentDictionary<AssetId, byte> _planesUnsupported = new();
    private readonly ConcurrentDictionary<(AssetId Asset, HardwareVideoDevice Device), DecoderSlot?> _gpuDecoders = new();
    private readonly ConcurrentDictionary<AssetId, byte> _gpuUnsupported = new();

    // プレビューで使うプロキシ（書き出しでは渡さないので、使わない）
    private readonly ProxyQueue? _proxies;
    private readonly ConcurrentDictionary<string, byte> _proxyRequested = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _useProxies = true;

    // 平面のまま渡すデコーダーの段階の番号（縮小しない）
    private const int PlanesStep = -1;
    private readonly ColorTransforms _transforms;
    private readonly ConcurrentDictionary<string, Lazy<PcmAudio?>> _voices = new(StringComparer.Ordinal);
    private long _imageBytes;

    /// <param name="quality">動画の RGB への変換の質（プレビューは速さ、書き出しは質を優先する）</param>
    /// <param name="proxies">
    /// プレビューで使うプロキシ（プロジェクトのプロキシの設定に合う動画は、プロキシがあればそれを読み、なければ作り始めて、できるまでは元の素材を読む）。
    /// 書き出しでは渡さない（必ず元の素材を読む）
    /// </param>
    public ProjectMediaSource(
        Project project, string projectFolder, MediaCache cache, ConversionQuality quality = ConversionQuality.Fast, ColorTransforms? transforms = null,
        ProxyQueue? proxies = null)
    {
        _project = project;
        _proxies = proxies;
        if (proxies is not null)
        {
            proxies.JobChanged += OnProxyChanged;
        }

        _transforms = transforms ?? ColorTransforms.Shared;
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

    /// <summary>
    /// 動画のフレームを YUV の平面のまま（GPU 版が使う。縮小しない）。4:2:0 でない動画など、平面で渡せなければ null
    /// （一度渡せなかった素材は、それからは試さない）
    /// </summary>
    public VideoPlaneFrame? GetVideoPlanes(AssetId asset, Rational time)
    {
        if (_planesUnsupported.ContainsKey(asset))
        {
            return null;
        }

        var slot = GetOrOpen<(AssetId Asset, int Step)>(_decoders, (asset, PlanesStep), key => OpenDecoder(key.Asset, key.Step));
        if (slot is null)
        {
            return null;
        }

        lock (slot.Gate)
        {
            try
            {
                var frame = slot.Decoder.GetFrame(time);
                if (frame.Planes is not { } planes)
                {
                    _planesUnsupported[asset] = 0;
                    return null;
                }

                return new VideoPlaneFrame(planes, slot.SourceWidth, slot.SourceHeight, InputLut(asset));
            }
            catch (Exception ex) when (ex is MediaException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 動画のフレームを、描画と同じ GPU でデコードしたフレームのまま（Vulkan Video）。GPU でデコードできない動画は null
    /// （一度できなかった素材は、それからは試さない）
    /// </summary>
    public GpuVideoFrame? GetGpuVideoFrame(AssetId asset, Rational time, HardwareVideoDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_gpuUnsupported.ContainsKey(asset))
        {
            return null;
        }

        var slot = GetOrOpen<(AssetId Asset, HardwareVideoDevice Device)>(_gpuDecoders, (asset, device), key =>
        {
            // 描画をやめるときは、このデバイスのデコーダーを先に捨てる
            key.Device.Closing += () => CloseGpuDecoders(key.Device);
            return OpenGpuDecoder(key.Asset, key.Device);
        });
        if (slot is null)
        {
            return null;
        }

        lock (slot.Gate)
        {
            try
            {
                var frame = slot.Decoder.GetFrame(time);
                if (frame.GpuFrame is not { } gpu)
                {
                    _gpuUnsupported[asset] = 0;
                    return null;
                }

                return new GpuVideoFrame(gpu.Clone(), slot.SourceWidth, slot.SourceHeight, InputLut(asset));
            }
            catch (Exception ex) when (ex is MediaException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    private DecoderSlot? OpenGpuDecoder(AssetId asset, HardwareVideoDevice device)
    {
        if (VideoPath(asset) is not { } source)
        {
            return null;
        }

        try
        {
            return new DecoderSlot(
                VideoDecoder.Open(source.Path, new VideoDecoderOptions(CacheCapacity: 4, Output: VideoFrameOutput.Gpu, VulkanDevice: device.Reference)),
                source.Width,
                source.Height);
        }
        catch (MediaException)
        {
            return null;
        }
    }

    private void CloseGpuDecoders(HardwareVideoDevice device)
    {
        foreach (var key in _gpuDecoders.Keys.Where(key => key.Device == device))
        {
            CloseSlot(_gpuDecoders, key);
        }
    }

    // デコーダーを、なければ開いて覚える。いくつものスレッドが同時に開いたときは、覚えなかったほうを閉じる（ファイルを開いたままにしない）
    private static DecoderSlot? GetOrOpen<TKey>(ConcurrentDictionary<TKey, DecoderSlot?> decoders, TKey key, Func<TKey, DecoderSlot?> open)
        where TKey : notnull
    {
        if (decoders.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var opened = open(key);
        var kept = decoders.GetOrAdd(key, opened);
        if (!ReferenceEquals(kept, opened))
        {
            opened?.Decoder.Dispose();
        }

        return kept;
    }

    private static void CloseSlot<TKey>(ConcurrentDictionary<TKey, DecoderSlot?> decoders, TKey key)
        where TKey : notnull
    {
        if (decoders.TryRemove(key, out var slot) && slot is not null)
        {
            lock (slot.Gate)
            {
                slot.Decoder.Dispose();
            }
        }
    }

    public SourceImage? GetVideoFrame(AssetId asset, Rational time, double scale)
    {
        var slot = GetOrOpen<(AssetId Asset, int Step)>(_decoders, (asset, StepFor(scale)), key => OpenDecoder(key.Asset, key.Step));
        if (slot is null)
        {
            return null;
        }

        lock (slot.Gate)
        {
            try
            {
                var image = slot.Decoder.GetFrame(time).Bgra;
                if (InputLut(asset) is { } lut)
                {
                    image = ColorLutImage.Apply(lut, image);
                }

                return new SourceImage(image, slot.SourceWidth, slot.SourceHeight);
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

        // プロキシを使う範囲が変わったら、動画を読み直す（プロキシと元の素材のどちらを読むかが変わる）
        if (previous.Proxies.Mode != project.Proxies.Mode)
        {
            CloseVideoDecoders(_ => true);
        }

        // 色の構成が変わったら、色を変換した画像をすべて作り直す
        if (previous.Color.OcioConfig != project.Color.OcioConfig)
        {
            foreach (var id in _inputLuts.Keys)
            {
                ForgetConvertedImages(id);
            }
        }

        foreach (var (id, asset) in previous.Assets)
        {
            if (project.Assets.TryGetValue(id, out var current) && current.Path == asset.Path)
            {
                if (current.ColorSpace != asset.ColorSpace)
                {
                    ForgetConvertedImages(id);
                }

                continue;
            }

            _inputLuts.TryRemove(id, out _);
            _inputErrors.TryRemove(id, out _);
            _planesUnsupported.TryRemove(id, out _);
            _gpuUnsupported.TryRemove(id, out _);
            foreach (var key in _gpuDecoders.Keys.Where(key => key.Asset == id))
            {
                CloseSlot(_gpuDecoders, key);
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
        if (_proxies is not null)
        {
            _proxies.JobChanged -= OnProxyChanged;
        }

        foreach (var slot in _decoders.Values.Concat(_gpuDecoders.Values))
        {
            slot?.Decoder.Dispose();
        }

        _decoders.Clear();
        _gpuDecoders.Clear();
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

    /// <summary>
    /// 動画の素材を、今どの方式でデコードしているか（利用者に表示する文。まだデコードしていなければ null）。
    /// 「Vulkan Video（GPU のまま）」「d3d11va（YUV の平面）」「ソフトウェア（BGRA）」のように、デコードの方式と渡し方を書く
    /// </summary>
    public string? VideoDecodeMethod(AssetId asset)
    {
        static string Name(VideoDecoder decoder) => decoder.HardwareDevice is { } device ? device == "vulkan" ? "Vulkan Video" : device : "ソフトウェア";
        if (!_gpuUnsupported.ContainsKey(asset) && _gpuDecoders.FirstOrDefault(pair => pair.Key.Asset == asset).Value is { } gpu)
        {
            return $"{Name(gpu.Decoder)}（GPU のまま）";
        }

        if (!_planesUnsupported.ContainsKey(asset) && _decoders.TryGetValue((asset, PlanesStep), out var planes) && planes is not null)
        {
            return $"{Name(planes.Decoder)}（YUV の平面）";
        }

        return _decoders.FirstOrDefault(pair => pair.Key.Asset == asset && pair.Key.Step >= 0).Value is { } bgra ? $"{Name(bgra.Decoder)}（BGRA）" : null;
    }

    /// <summary>素材の入力の色空間を変換できなかった理由（変換しないか、できたなら null）</summary>
    public string? InputColorError(AssetId asset)
    {
        InputLut(asset);
        return _inputErrors.GetValueOrDefault(asset);
    }

    private SourceImage? LoadImage(AssetId asset, int step)
    {
        if (PathOf(asset) is not { } path || LoadImageFile(path, step) is not { } image)
        {
            return null;
        }

        return InputLut(asset) is { } lut ? image with { Image = ColorLutImage.Apply(lut, image.Image) } : image;
    }

    // 素材の入力の色空間（Asset.ColorSpace）から作業用の sRGB への変換。指定がなければ、動画の色のタグから決める
    // （PQ の HDR・P3 の動画）。変換しなくてよい・変換できないときは null（そのまま描く）
    private ColorLut? InputLut(AssetId asset)
    {
        var project = _project;
        if (!project.Assets.TryGetValue(asset, out var info) || (info.ColorSpace ?? AutomaticInput(info)) is not { } space)
        {
            return null;
        }

        return _inputLuts.GetOrAdd(asset, id =>
        {
            if (!Ocio.TryLoad(out var error))
            {
                _inputErrors[id] = error ?? "OpenColorIO を使えません";
                return null;
            }

            try
            {
                _inputErrors.TryRemove(id, out _);
                return _transforms.Input(project.Color.OcioConfig, space);
            }
            catch (OcioException ex)
            {
                _inputErrors[id] = ex.Message;
                return null;
            }
        });
    }

    // 動画の色のタグから決めた入力の色空間（OCIO の名前）。画像と、変換しなくてよい動画は null
    private string? AutomaticInput(Asset asset) =>
        asset.Kind == AssetKind.Video && PathOf(asset.Id) is { } path && Info(path)?.Video is { } video
            ? ColorTransforms.AutomaticInput(video.Color.SuggestedInputColorSpace)
            : null;

    // 色の変換が変わった素材の、変換した画像（覚えている画像と、変換の表）を捨てる
    private void ForgetConvertedImages(AssetId id)
    {
        _inputLuts.TryRemove(id, out _);
        _inputErrors.TryRemove(id, out _);
        foreach (var key in _images.Keys.Where(key => key.Asset == id))
        {
            _images.TryRemove(key, out _);
        }
    }

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
        if (VideoPath(asset) is not { } source)
        {
            return null;
        }

        try
        {
            var video = (Width: source.Width, Height: source.Height);
            var options = step == PlanesStep
                ? new VideoDecoderOptions(Output: VideoFrameOutput.Planes)
                : new VideoDecoderOptions(MaxSize(video.Width, step), MaxSize(video.Height, step), Quality: _quality);
            return new DecoderSlot(VideoDecoder.Open(source.Path, options), source.Width, source.Height);
        }
        catch (MediaException)
        {
            return null;
        }
    }

    private static int MaxSize(int size, int step) => Math.Max(1, (int)Math.Ceiling(size * Steps[step]));

    /// <summary>
    /// プレビューでプロキシを使うか（プレビューでの一時的な切り替え。プロジェクトのプロキシの設定とは別）。変えると、動画を読み直す
    /// </summary>
    public bool UseProxies
    {
        get => _useProxies;
        set
        {
            if (_useProxies != value)
            {
                _useProxies = value;
                CloseVideoDecoders(_ => true);
            }
        }
    }

    /// <summary>素材の動画のプロキシがあるか</summary>
    public bool HasProxy(AssetId asset) => _proxies is not null && PathOf(asset) is { } path && _proxies.FindProxy(path) is not null;

    /// <summary>素材の動画のプロキシを作る仕事を加える（プロジェクトの設定に関わらず作る）。素材が見つからなければ null</summary>
    public ProxyJob? CreateProxy(AssetId asset) =>
        _proxies is not null && PathOf(asset) is { } path && File.Exists(path) ? _proxies.Enqueue(path) : null;

    /// <summary>素材の動画のプロキシを消す（読んでいれば閉じてから。作っている途中ならキャンセルする）。このあとは、自動では作り直さない</summary>
    /// <exception cref="IOException">プロキシを消せない</exception>
    public bool DeleteProxy(AssetId asset)
    {
        if (_proxies is null || PathOf(asset) is not { } path)
        {
            return false;
        }

        _proxyRequested.TryAdd(Path.GetFullPath(path), 0);
        CloseVideoDecoders(id => id == asset);
        return _proxies.Delete(path);
    }

    // 動画を読む場所と、素材の元の大きさ（プロキシを読むときも、大きさは元の素材のまま扱う）。
    // プロキシを使う設定（使わない以外）なら、プロキシがあればプロキシを読む。設定に合う大きさの動画は、なければ作り始めて、できるまでは元の素材
    private (string Path, int Width, int Height)? VideoPath(AssetId asset)
    {
        if (PathOf(asset) is not { } path || Info(path)?.Video is not { } video)
        {
            return null;
        }

        var mode = _project.Proxies.Mode;
        if (_proxies is not null && _useProxies && mode != ProxyMode.Off)
        {
            if (_proxies.FindProxy(path) is { } proxy)
            {
                return (proxy, video.Width, video.Height);
            }

            if (UsesProxy(mode, video.Width, video.Height) && _proxyRequested.TryAdd(Path.GetFullPath(path), 0))
            {
                _proxies.Enqueue(path);
            }
        }

        return (path, video.Width, video.Height);
    }

    // 条件に合う素材の動画のデコーダーを閉じる（次に読むときに、読む場所を決め直す）
    private void CloseVideoDecoders(Func<AssetId, bool> match)
    {
        foreach (var key in _decoders.Keys.Where(key => match(key.Asset)))
        {
            CloseSlot(_decoders, key);
        }

        foreach (var key in _gpuDecoders.Keys.Where(key => match(key.Asset)))
        {
            CloseSlot(_gpuDecoders, key);
        }
    }

    // プロキシを使う大きさか（1080p より大きい動画だけ、またはすべての動画）
    private static bool UsesProxy(ProxyMode mode, int width, int height) => mode switch
    {
        ProxyMode.AllVideos => true,
        ProxyMode.LargeVideos => Math.Max(width, height) > 1920 || Math.Min(width, height) > 1080,
        _ => false,
    };

    /// <summary>素材の動画を、今プロキシで読んでいるか</summary>
    public bool IsUsingProxy(AssetId asset) =>
        _useProxies && _project.Proxies.Mode != ProxyMode.Off && HasProxy(asset) && PathOf(asset) is { } path && Info(path)?.Video is not null;

    // プロキシができたら、その素材のデコーダーを閉じて、次から読み直させる（プロキシを読むように）
    private void OnProxyChanged(object? sender, ProxyJob job)
    {
        if (job.State != ProxyJobState.Completed)
        {
            return;
        }

        foreach (var (id, asset) in _project.Assets)
        {
            if (asset.Kind == AssetKind.Video && PathOf(id) is { } path
                && string.Equals(Path.GetFullPath(path), job.SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                CloseVideoDecoders(other => other == id);
            }
        }
    }

    private sealed record DecoderSlot(VideoDecoder Decoder, int SourceWidth, int SourceHeight)
    {
        public Lock Gate { get; } = new();
    }
}
