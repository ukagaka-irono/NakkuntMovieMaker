using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Text;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// Vulkan での GPU 描画。作業用の画像（premultiplied の RGBA を 16 ビット浮動小数点で持つバッファー）に、ノードを下から順に重ねて、最後に BGRA（8 ビット）にして読み戻す。
/// 素材の画像（静止画・動画のフレーム）は GPU で合成する。まだ GPU で描けないもの（文字・図形・立ち絵・エフェクト・トランジション）は、
/// そのノードだけを CPU 版（Skia）で透明な画像に描いてから重ねる（M2 の手順 C-13・D で、GPU に移していく）。
/// 複数のスレッドから同時に呼んでよい（GPU への送信は GpuScheduler の 1 本のスレッドが行う）
/// </summary>
public sealed unsafe class VulkanBackend : IRenderBackend
{
    // CPU で描いたノードの画像を覚えておく量
    private const long LayerCacheBytes = 256L << 20;

    private static readonly GpuBindingKind[] ClearBindings = [GpuBindingKind.StorageBuffer];
    internal static readonly GpuBindingKind[] CompositeBindings = [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer];
    private static readonly GpuBindingKind[] ConvertBindings = [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer];

    private readonly VulkanDevice _device;
    private readonly bool _ownsDevice;
    private readonly GpuAllocator _allocator;
    private readonly GpuScheduler _scheduler;
    private readonly ShaderLibrary _shaders;
    private readonly TexturePool _pool;
    private readonly GpuCache<BgraImage> _sources;
    private readonly GpuCache<EffectKey> _effectResults;
    private readonly ConcurrentDictionary<EffectKey, byte> _effectKnown = new();
    private long _gpuEffectNodes;
    private long _cpuLayers;
    private long _gpuTransitionNodes;

    // 今記録している仕事の作業用の画像がリニアか（GPU のスレッドだけで使う）
    private bool _linear;
    private readonly SkiaCpuBackend _cpu;
    private readonly ConcurrentDictionary<LayerKey, Layer?> _layers = new();
    private long _layerBytes;
    private bool _disposed;

    /// <param name="device">使う GPU（null なら選んで作る。そのときは破棄するときに一緒に破棄する）</param>
    /// <param name="layouts">文字の配置のキャッシュ（大きさを測る処理と共有するとき）</param>
    /// <exception cref="VulkanException">GPU を使えない</exception>
    /// <param name="ownsDevice">渡した device も、破棄するときに一緒に破棄するか</param>
    public VulkanBackend(VulkanDevice? device = null, TextLayoutCache? layouts = null, bool ownsDevice = false, string? pipelineCachePath = null)
    {
        _ownsDevice = device is null || ownsDevice;
        _device = device ?? VulkanDevice.Create();
        try
        {
            _allocator = new GpuAllocator(_device);
            _scheduler = new GpuScheduler(_device);
            _shaders = new ShaderLibrary(_device, pipelineCachePath);
            _pool = new TexturePool(_allocator, _allocator.BudgetBytes / 4);
            _sources = new GpuCache<BgraImage>(_allocator.BudgetBytes / 2);
            _effectResults = new GpuCache<EffectKey>(_allocator.BudgetBytes / 4);
            _cpu = new SkiaCpuBackend(layouts: layouts);
        }
        catch
        {
            if (_ownsDevice)
            {
                _device.Dispose();
            }

            throw;
        }

        Info = new RenderBackendInfo(RenderBackendKind.Vulkan, $"Vulkan（{_device.Info.Name}）", _device.Info.Name, PreferredConcurrency: 3);
    }

    public RenderBackendInfo Info { get; }

    /// <summary>エフェクトを GPU でかけたノードの数（これまでの合計。テストと計測用）</summary>
    public long GpuEffectNodes => Interlocked.Read(ref _gpuEffectNodes);

    /// <summary>トランジションを GPU で描いたノードの数（これまでの合計。テストと計測用）</summary>
    public long GpuTransitionNodes => Interlocked.Read(ref _gpuTransitionNodes);

    /// <summary>CPU で描いてから GPU で重ねたノードの数（これまでの合計。テストと計測用）</summary>
    public long CpuLayers => Interlocked.Read(ref _cpuLayers);

    public VulkanDevice Device => _device;

    public RenderResult Render(FramePlan plan, IMediaSource media, RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scale = request.Scale;
        if (!(scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "倍率は 0 より大きくしてください");
        }

        // CPU 版と同じ大きさにする
        var width = Math.Max(1, (int)Math.Round(plan.Width * scale));
        var height = Math.Max(1, (int)Math.Round(plan.Height * scale));
        var output = Matrix3x2.CreateScale((float)(width / (double)plan.Width), (float)(height / (double)plan.Height));

        // 素材を取り出す・CPU で描くのは、呼び出したスレッドで行う（GPU のスレッドを待たせない）
        var operations = new List<object>();
        foreach (var node in plan.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Prepare(node, plan, media, output, width, height, scale) is { } operation)
            {
                operations.Add(operation);
            }
        }

        var linear = plan.Color.IsLinear;
        var background = linear ? LinearPremultiplied(plan.Background) : Premultiplied(plan.Background);
        var task = _scheduler.RunAsync(context => Record(context, operations, background, width, height, linear), cancellationToken);
        return task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.Dispose();
        _sources.Dispose();
        _effectResults.Dispose();
        _pool.Dispose();
        _shaders.Dispose();
        _allocator.Dispose();
        _cpu.Dispose();
        if (_ownsDevice)
        {
            _device.Dispose();
        }
    }

    // ノードを、GPU で重ねる処理にする。GPU で描けないものは CPU で描いた画像にする
    private object? Prepare(RenderNode node, FramePlan plan, IMediaSource media, Matrix3x2 output, int width, int height, double scale)
    {
        if (node is DrawNode { Effects.IsEmpty: true, Content: ImageContent or VideoFrameContent } draw)
        {
            return PrepareImage(draw, media, output, width, height);
        }

        // トランジション：前後のアイテムを、それぞれ出力と同じ大きさの画像に GPU で描いてから、GPU で混ぜる
        if (node is TransitionNode transition && GpuTransitions.Find(transition.Type) is { } processor)
        {
            Interlocked.Increment(ref _gpuTransitionNodes);
            var from = transition.From is null ? null : Prepare(transition.From, plan, media, output, width, height, scale);
            var to = transition.To is null ? null : Prepare(transition.To, plan, media, output, width, height, scale);
            return new TransitionStep(processor, (float)Math.Clamp(transition.Progress, 0, 1), transition.Parameters, from, to, media);
        }

        if (node is DrawNode { Effects.IsEmpty: false } withEffects && GpuEffects.Supports(withEffects.Effects))
        {
            Interlocked.Increment(ref _gpuEffectNodes);
            return PrepareEffects(withEffects, plan, media, output, scale);
        }

        // まだ GPU で描けないもの：そのノードだけを、描かれる範囲だけ CPU で透明な画像に描き、合成モードと不透明度は GPU で重ねるときに付ける。
        // 動画を含まないものは、描いた画像を覚えておく（同じ画像なら、GPU に送り直さずに済む）
        var (alone, blend, opacity) = node switch
        {
            DrawNode d => ((RenderNode)(d with { Blend = BlendMode.Normal, Opacity = 1 }), d.Blend, Round(d.Opacity)),
            _ => (node, BlendMode.Normal, 1f),
        };
        var key = new LayerKey(alone, scale, plan.Width, plan.Height);
        Interlocked.Increment(ref _cpuLayers);
        var reusable = IsStill(alone);
        if (!reusable || !_layers.TryGetValue(key, out var layer))
        {
            layer = _cpu.NodeRegion(alone, plan, media, scale) is { } area
                ? new Layer(_cpu.RenderRegion(alone, plan, media, scale, area), area)
                : null;
            if (reusable)
            {
                AddLayer(key, layer);
            }
        }

        if (layer is null)
        {
            return null;
        }

        var region = layer.Region;
        var destination = new Rect(region.X, region.Y, region.Width, region.Height);
        return new Operation(
            layer.Image, Owned: !reusable, Matrix3x2.Identity, destination, new SourceRect(0, 0, region.Width, region.Height), opacity, blend, Antialias: false,
            new Region(region.X, region.Y, region.Width, region.Height));
    }

    // エフェクトをかけて描くノード：中身を CPU 版と同じ細かさの画像にし（静止したものは結果を覚えておく）、エフェクトは GPU でかける
    private EffectStep? PrepareEffects(DrawNode node, FramePlan plan, IMediaSource media, Matrix3x2 output, double scale)
    {
        var matrix = node.Transform * output;
        var hint = Math.Max(Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12), Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22));
        if (!Matrix3x2.Invert(matrix, out var inverse))
        {
            return null;
        }

        // 結果を覚えておく鍵は CPU 版と同じ（素材・切り抜き・エフェクト・細かさ）。動画は毎フレーム変わるので覚えない
        EffectKey? key = null;
        if (node.Content is not VideoFrameContent)
        {
            var identity = node.Content is ImageContent image && media.GetImage(image.Asset, hint) is { } source ? source : (object)node.Content;
            key = new EffectKey(identity, node.Crop, node.Effects, (float)hint);
        }

        var raster = key is not null && _effectKnown.ContainsKey(key) ? null : _cpu.RasterizeForEffects(node, plan, media, scale);
        if (raster is null && key is null)
        {
            return null;
        }

        var opacity = node.Opacity < 1 || node.Blend != BlendMode.Normal ? Round(node.Opacity) : 1f;
        return new EffectStep(node, matrix, inverse, key, raster, () => _cpu.RasterizeForEffects(node, plan, media, scale), media, opacity);
    }

    // GPU のスレッドで：エフェクトをかけた結果（覚えていれば、それを使う）。描くものがなければ null
    private GpuEffectImage? EffectResult(GpuCommandContext context, EffectStep step, out bool owned)
    {
        owned = false;
        if (step.Key is not null && _effectResults.TryGet<GpuEffectImage>(step.Key, context.Value, out var cached))
        {
            return cached;
        }

        if ((step.Raster ?? step.LateRaster()) is not { } raster)
        {
            return null;
        }

        // CPU で描いた画像を GPU に送り、16 ビット浮動小数点の作業用の画像にする
        var effects = new GpuEffectContext(context, _allocator, _shaders, step.Media);
        var pixels = raster.Image.Pixels;
        var upload = _allocator.CreateBuffer((ulong)pixels.Length, VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Upload);
        pixels.CopyTo(upload.Span);
        upload.Flush();
        context.OnCompleted(upload.Dispose);
        var image = effects.CreateImage(raster.PixelLeft, raster.PixelTop, raster.Image.Width, raster.Image.Height, raster.Scale);
        _shaders.Get("fx_from_bgra8", ConvertBindings, sizeof(int))
            .Dispatch(context, [upload, image.Buffer], image.Count, ComputePipeline.Groups(image.Count, 256), 1);
        GpuBarriers.AfterCompute(context);

        foreach (var effect in step.Node.Effects)
        {
            if (GpuEffects.Find(effect.Type) is { } processor)
            {
                image = processor.Apply(effects, image, effect.Parameters);
            }
        }

        if (step.Key is null)
        {
            owned = true;
            return image;
        }

        _effectResults.Add(step.Key, image, image.Bytes, context.Value);
        if (_effectKnown.Count > 10_000)
        {
            _effectKnown.Clear();
        }

        _effectKnown[step.Key] = 0;
        return image;
    }

    // 素材を切り抜き、残った部分の中心をローカル座標の原点に置いて描く（SkiaCpuBackend.ImageContent と同じ）
    private static Operation? PrepareImage(DrawNode node, IMediaSource media, Matrix3x2 output, int width, int height)
    {
        var matrix = node.Transform * output;
        var hint = Math.Max(Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12), Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22));
        var source = node.Content switch
        {
            VideoFrameContent video => media.GetVideoFrame(video.Asset, video.SourceTime, hint),
            ImageContent picture => media.GetImage(picture.Asset, hint),
            _ => null,
        };
        if (source is null || !Matrix3x2.Invert(matrix, out var inverse))
        {
            return null;
        }

        var crop = node.Crop;
        var visibleWidth = source.Width - crop.Left - crop.Right;
        var visibleHeight = source.Height - crop.Top - crop.Bottom;
        if (visibleWidth <= 0 || visibleHeight <= 0)
        {
            return null;
        }

        var image = source.Image;
        var ratioX = image.Width / (double)source.Width;
        var ratioY = image.Height / (double)source.Height;
        var left = Math.Clamp((int)Math.Round(crop.Left * ratioX), 0, image.Width);
        var top = Math.Clamp((int)Math.Round(crop.Top * ratioY), 0, image.Height);
        var right = Math.Clamp((int)Math.Round((source.Width - crop.Right) * ratioX), 0, image.Width);
        var bottom = Math.Clamp((int)Math.Round((source.Height - crop.Bottom) * ratioY), 0, image.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        var destination = new Rect((float)(-visibleWidth / 2), (float)(-visibleHeight / 2), (float)visibleWidth, (float)visibleHeight);
        if (Bounds(destination, matrix, width, height) is not { } region)
        {
            return null;
        }

        var opacity = node.Opacity < 1 || node.Blend != BlendMode.Normal ? Round(node.Opacity) : 1f;
        var owned = node.Content is VideoFrameContent;
        return new Operation(image, owned, inverse, destination, new SourceRect(left, top, right - left, bottom - top), opacity, node.Blend, Antialias: true, region);
    }

    // GPU のスレッドで：作業用の画像を背景で塗り、ノードを順に重ねて、BGRA にして読み戻す
    private VulkanRenderResult Record(GpuCommandContext context, List<object> steps, Vector4 background, int width, int height, bool linear)
    {
        _linear = linear;
        _sources.Collect(_scheduler.CompletedValue);
        _effectResults.Collect(_scheduler.CompletedValue);

        // 作業用の画像は、どの GPU でも使えるようにバッファーにする（1 画素 8 バイト）。この仕事が終わったら捨てる
        var work = _allocator.CreateBuffer((ulong)width * (ulong)height * 8, VkBufferUsageFlags.StorageBuffer, GpuBufferKind.DeviceLocal);
        context.OnCompleted(work.Dispose);
        var clear = _shaders.Get("clear", ClearBindings, sizeof(ClearParams));
        var pixels = width * height;
        clear.Dispatch(context, [work], new ClearParams { R = background.X, G = background.Y, B = background.Z, A = background.W, Count = pixels }, ComputePipeline.Groups(pixels, 256), 1);
        GpuBarriers.AfterCompute(context);

        var composite = _shaders.Get("composite", CompositeBindings, sizeof(CompositeParams));
        foreach (var step in steps)
        {
            RecordStep(context, composite, work, step, width, height);
        }

        var readback = _allocator.CreateBuffer((ulong)(width * height * 4), VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Readback);
        var convert = _shaders.Get("to_bgra8", ConvertBindings, sizeof(ConvertParams));
        convert.Dispatch(context, [work, readback], new ConvertParams { Width = width, Height = height, EncodeSrgb = linear ? 1 : 0 }, ComputePipeline.Groups(width, 8), ComputePipeline.Groups(height, 8));
        GpuBarriers.AfterCompute(context);
        return new VulkanRenderResult(context.Fence, readback, width, height);
    }

    // ノード 1 つを、作業用の画像 work（出力と同じ大きさ）に重ねる
    private void RecordStep(GpuCommandContext context, ComputePipeline composite, GpuBuffer work, object step, int width, int height)
    {
        switch (step)
        {
            case EffectStep effectStep:
                CompositeEffect(context, composite, work, effectStep, width, height);
                break;
            case TransitionStep transitionStep:
                RecordTransition(context, composite, work, transitionStep, width, height);
                break;
            case Operation operation:
                CompositeOperation(context, composite, work, operation, width);
                break;
        }
    }

    // トランジション：前後のアイテムを、それぞれ透明な画像に描いてから混ぜる（CPU 版の DrawTransition と同じ）
    private void RecordTransition(GpuCommandContext context, ComputePipeline composite, GpuBuffer work, TransitionStep step, int width, int height)
    {
        var effects = new GpuEffectContext(context, _allocator, _shaders, step.Media) { Linear = _linear };
        var from = effects.CreateClearImage(0, 0, width, height, 1);
        var to = effects.CreateClearImage(0, 0, width, height, 1);
        effects.Release(from);
        effects.Release(to);
        if (step.From is not null)
        {
            RecordStep(context, composite, from.Buffer, step.From, width, height);
        }

        if (step.To is not null)
        {
            RecordStep(context, composite, to.Buffer, step.To, width, height);
        }

        // 出力の作業用の画像は、この仕事の終わりに捨てるので、ここでは包むだけ
        var target = new GpuEffectImage(work, width, height, 0, 0, 1);
        step.Processor.Draw(effects, target, from, to, step.Progress, step.Parameters);
    }

    // 素材の画像（の一部）を、変形・不透明度・合成モードを付けて重ねる
    private void CompositeOperation(GpuCommandContext context, ComputePipeline composite, GpuBuffer work, Operation operation, int width)
    {
        var source = SourceBuffer(context, operation);
        var region = operation.Region;
        var parameters = new CompositeParams
        {
            Ax = operation.Inverse.M11,
            Ay = operation.Inverse.M12,
            Bx = operation.Inverse.M21,
            By = operation.Inverse.M22,
            Cx = operation.Inverse.M31,
            Cy = operation.Inverse.M32,
            Left = operation.Destination.Left,
            Top = operation.Destination.Top,
            Width = operation.Destination.Width,
            Height = operation.Destination.Height,
            SourceX = operation.Source.X,
            SourceY = operation.Source.Y,
            SourceWidth = operation.Source.Width,
            SourceHeight = operation.Source.Height,
            Stride = operation.Image.Width,
            OriginX = region.X,
            OriginY = region.Y,
            RegionWidth = region.Width,
            RegionHeight = region.Height,
            Opacity = operation.Opacity,
            Blend = (int)operation.Blend,
            Antialias = operation.Antialias ? 1 : 0,
            Linearize = _linear ? 1 : 0,
            TargetWidth = width,
        };
        composite.Dispatch(context, [work, source], parameters, ComputePipeline.Groups(region.Width, 8), ComputePipeline.Groups(region.Height, 8));
        GpuBarriers.AfterCompute(context);
    }

    // エフェクトをかけた結果を、変形・不透明度・合成モードを付けて重ねる（CPU 版と同じく、ローカル座標の範囲に、縁のアンチエイリアス付きで描く）
    private void CompositeEffect(GpuCommandContext context, ComputePipeline composite, GpuBuffer work, EffectStep step, int width, int height)
    {
        if (EffectResult(context, step, out var owned) is not { } result)
        {
            return;
        }

        if (owned)
        {
            context.OnCompleted(result.Dispose);
        }

        var bounds = result.LocalBounds;
        var destination = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (Bounds(destination, step.Matrix, width, height) is not { } region)
        {
            return;
        }

        var parameters = new CompositeParams
        {
            Ax = step.Inverse.M11,
            Ay = step.Inverse.M12,
            Bx = step.Inverse.M21,
            By = step.Inverse.M22,
            Cx = step.Inverse.M31,
            Cy = step.Inverse.M32,
            Left = destination.Left,
            Top = destination.Top,
            Width = destination.Width,
            Height = destination.Height,
            SourceWidth = result.Width,
            SourceHeight = result.Height,
            Stride = result.Width,
            OriginX = region.X,
            OriginY = region.Y,
            RegionWidth = region.Width,
            RegionHeight = region.Height,
            Opacity = step.Opacity,
            Blend = (int)step.Node.Blend,
            Antialias = 1,
            TargetWidth = width,
            SourceFormat = 1,
            Linearize = _linear ? 1 : 0,
        };
        composite.Dispatch(context, [work, result.Buffer], parameters, ComputePipeline.Groups(region.Width, 8), ComputePipeline.Groups(region.Height, 8));
        GpuBarriers.AfterCompute(context);
    }

    // 素材の画素を GPU のバッファーにする。静止画は覚えておき、動画のフレームと CPU で描いた画像は、この仕事が終わったら捨てる
    private GpuBuffer SourceBuffer(GpuCommandContext context, Operation operation)
    {
        if (!operation.Owned && _sources.TryGet<GpuBuffer>(operation.Image, context.Value, out var cached))
        {
            return cached;
        }

        var pixels = operation.Image.Pixels;
        var buffer = _allocator.CreateBuffer((ulong)pixels.Length, VkBufferUsageFlags.StorageBuffer, GpuBufferKind.Upload);
        pixels.CopyTo(buffer.Span);
        buffer.Flush();
        if (operation.Owned)
        {
            context.OnCompleted(buffer.Dispose);
        }
        else
        {
            _sources.Add(operation.Image, buffer, buffer.Bytes, context.Value);
        }

        return buffer;
    }

    // 動画のフレームを含まない（同じノードなら、毎回同じ画像になる）か
    private static bool IsStill(RenderNode? node) => node switch
    {
        null => true,
        DrawNode draw => draw.Content is not VideoFrameContent,
        TransitionNode transition => IsStill(transition.From) && IsStill(transition.To),
        _ => false,
    };

    // CPU で描いた画像を覚える。多くなりすぎたら、すべて捨てる（GPU に送ったものは、GPU のキャッシュが別に覚えている）
    private void AddLayer(LayerKey key, Layer? layer)
    {
        var bytes = layer?.Image.Pixels.Length ?? 0;
        if (Interlocked.Add(ref _layerBytes, bytes) > LayerCacheBytes)
        {
            _layers.Clear();
            Interlocked.Exchange(ref _layerBytes, bytes);
        }

        _layers[key] = layer;
    }

    // 四角形を変換した先を囲む、出力の中の画素の範囲（縁のアンチエイリアスの分を足す）。出力の外なら null
    private static Region? Bounds(Rect rect, Matrix3x2 matrix, int width, int height)
    {
        Span<Vector2> corners =
        [
            Vector2.Transform(new Vector2(rect.Left, rect.Top), matrix),
            Vector2.Transform(new Vector2(rect.Left + rect.Width, rect.Top), matrix),
            Vector2.Transform(new Vector2(rect.Left, rect.Top + rect.Height), matrix),
            Vector2.Transform(new Vector2(rect.Left + rect.Width, rect.Top + rect.Height), matrix),
        ];
        var min = Vector2.Min(Vector2.Min(corners[0], corners[1]), Vector2.Min(corners[2], corners[3]));
        var max = Vector2.Max(Vector2.Max(corners[0], corners[1]), Vector2.Max(corners[2], corners[3]));
        var x0 = Math.Max(0, (int)Math.Floor(min.X) - 1);
        var y0 = Math.Max(0, (int)Math.Floor(min.Y) - 1);
        var x1 = Math.Min(width, (int)Math.Ceiling(max.X) + 1);
        var y1 = Math.Min(height, (int)Math.Ceiling(max.Y) + 1);
        return x1 > x0 && y1 > y0 ? new Region(x0, y0, x1 - x0, y1 - y0) : null;
    }

    // 不透明度は、CPU 版（Skia の 8 ビットの不透明度）と同じく 1/255 の単位にする
    private static float Round(double opacity) => (float)(Math.Round(Math.Clamp(opacity, 0, 1) * 255) / 255);

    // 背景（straight alpha の 8 ビット）を premultiplied にする（Skia の Clear と同じく、8 ビットで四捨五入）
    // リニアのモードの背景：Skia と同じく、色をリニアにしてから premultiplied にする
    private static Vector4 LinearPremultiplied(RgbaColor color)
    {
        static float Linear(byte value)
        {
            var c = value / 255.0;
            return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }

        var alpha = color.A / 255f;
        return new Vector4(Linear(color.R) * alpha, Linear(color.G) * alpha, Linear(color.B) * alpha, alpha);
    }

    private static Vector4 Premultiplied(RgbaColor color)
    {
        static float Channel(byte value, byte alpha) => (float)((value * alpha + 127) / 255) / 255f;
        return new Vector4(Channel(color.R, color.A), Channel(color.G, color.A), Channel(color.B, color.A), color.A / 255f);
    }

    private readonly record struct Rect(float Left, float Top, float Width, float Height);

    // エフェクトの結果を覚えておく鍵（CPU 版の EffectKey と同じ考え方。Identity は素材の画像か、描く中身）
    private sealed record EffectKey(object Identity, Crop Crop, EquatableArray<EvaluatedEffect> Effects, float Hint);

    private sealed record EffectStep(
        DrawNode Node, Matrix3x2 Matrix, Matrix3x2 Inverse, EffectKey? Key, EffectSource? Raster, Func<EffectSource?> LateRaster, IMediaSource Media, float Opacity);

    private sealed record TransitionStep(IGpuTransition Processor, float Progress, EffectParameters Parameters, object? From, object? To, IMediaSource Media);

    private sealed record LayerKey(RenderNode Node, double Scale, int SceneWidth, int SceneHeight);

    // CPU で描いた、ノード 1 つの画像と、出力の中の位置
    private sealed record Layer(BgraImage Image, PixelRegion Region);

    private readonly record struct SourceRect(int X, int Y, int Width, int Height);

    private readonly record struct Region(int X, int Y, int Width, int Height);

    // GPU で重ねる処理：素材の画像（の一部）を、Destination（ローカル座標）に描く。Inverse はデバイスの座標 → ローカル座標
    private sealed record Operation(
        BgraImage Image, bool Owned, Matrix3x2 Inverse, Rect Destination, SourceRect Source, float Opacity, BlendMode Blend, bool Antialias, Region Region);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CompositeParams
    {
        public float Ax, Ay, Bx, By, Cx, Cy;
        public float Left, Top, Width, Height;
        public int SourceX, SourceY, SourceWidth, SourceHeight, Stride;
        public int OriginX, OriginY, RegionWidth, RegionHeight;
        public float Opacity;
        public int Blend, Antialias, TargetWidth;
        public int SourceFormat, Linearize, Padding1, Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClearParams
    {
        public float R, G, B, A;
        public int Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConvertParams
    {
        public int Width, Height, EncodeSrgb;
    }
}

/// <summary>GPU で描いた結果。ReadBgra で GPU の完了を待って読み出す</summary>
internal sealed class VulkanRenderResult(GpuFence fence, GpuBuffer buffer, int width, int height) : RenderResult
{
    private int _disposed;

    public override int Width => width;

    public override int Height => height;

    public override BgraImage ReadBgra()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        fence.Wait();
        buffer.Invalidate();
        return new BgraImage(width, height, buffer.Span.ToArray());
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // GPU がまだ書いているかもしれないので、終わるのを待ってから捨てる
            fence.Wait();
            buffer.Dispose();
        }
    }
}
