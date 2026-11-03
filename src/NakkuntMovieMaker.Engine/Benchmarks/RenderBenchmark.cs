using System.Collections.Immutable;
using System.Diagnostics;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Skia;
using SkiaSharp;

namespace NakkuntMovieMaker.Engine.Benchmarks;

/// <summary>計測する場面。Effects なら、3 つのレイヤーにぼかしと色調補正をかける</summary>
public sealed record BenchScenario(string Name, string Description, double Scale, bool Effects);

/// <summary>1 つの場面を計測した結果（時間はミリ秒）</summary>
public sealed record BenchResult(
    BenchScenario Scenario,
    int Frames,
    int Workers,
    double MeanMilliseconds,
    double P95Milliseconds,
    double MaxMilliseconds,
    double FramesPerSecond);

/// <summary>
/// 描画の速さを測る（性能スパイク）。1080p・30fps・8 レイヤーのシーン
/// （背景・動画・大きな写真・透過画像・図形・字幕・タイトル・立ち絵の代わりの透過画像）を作り、
/// 何枚か描いて 1 枚あたりの時間を測る。素材は作業フォルダに作り、2 回目からは使い回す
/// </summary>
public sealed class RenderBenchmark
{
    public const int Width = 1920;
    public const int Height = 1080;

    public static readonly ImmutableArray<BenchScenario> Scenarios =
    [
        new("full", "フル解像度・8 レイヤー", 1, false),
        new("half", "1/2 解像度・8 レイヤー", 0.5, false),
        new("half-effects", "1/2 解像度・3 レイヤーにぼかしと色調補正", 0.5, true),
        new("full-effects", "フル解像度・3 レイヤーにぼかしと色調補正", 1, true),
    ];

    private readonly string _folder;
    private readonly string? _video;
    private readonly SkiaCpuBackend _backend = new();

    /// <param name="folder">素材とキャッシュを置く作業フォルダ</param>
    /// <param name="video">使う動画（1080p の H.264 を想定）。null なら動画のレイヤーは空にする</param>
    public RenderBenchmark(string folder, string? video)
    {
        _folder = Path.GetFullPath(folder);
        _video = video is null ? null : Path.GetFullPath(video);
        Directory.CreateDirectory(_folder);
        CreateAssets();
    }

    public bool HasVideo => _video is not null;

    /// <summary>
    /// 場面を workers 個のスレッドで frames 枚描いて測る。最初の数枚（準備のため遅い）は計測に含めない。
    /// 複数のスレッドのときは、各スレッドが別々のフレームを描く（i、i + workers、…）
    /// </summary>
    public BenchResult Run(BenchScenario scenario, int frames, int workers = 1, CancellationToken cancellationToken = default)
    {
        var (project, scene) = CreateProject(scenario.Effects);
        using var media = new ProjectMediaSource(project, _folder, new MediaCache(Path.Combine(_folder, ".nmm", "cache")));

        // 準備（キャッシュの作成・デコーダーを開く・フォントの読み込み）
        for (var frame = 0; frame < 3; frame++)
        {
            Render(scene, frame, media, scenario.Scale, cancellationToken);
        }

        var times = new double[frames];
        var total = Stopwatch.StartNew();
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, worker =>
        {
            for (var i = worker; i < frames; i += workers)
            {
                var watch = Stopwatch.StartNew();
                Render(scene, 3 + i, media, scenario.Scale, cancellationToken);
                times[i] = watch.Elapsed.TotalMilliseconds;
            }
        });
        total.Stop();

        Array.Sort(times);
        return new BenchResult(
            scenario,
            frames,
            workers,
            times.Average(),
            times[(int)Math.Min(frames - 1, Math.Ceiling(frames * 0.95) - 1)],
            times[^1],
            frames / total.Elapsed.TotalSeconds);
    }

    /// <summary>止まっているときに、ばらばらの位置を表示する時間（ミリ秒の平均）。画像だけのシーンと、動画を含むシーン</summary>
    public (double ImagesOnly, double WithVideo) MeasureSeek(int count, CancellationToken cancellationToken = default)
    {
        var random = new Random(1234);
        double Measure(bool video)
        {
            var (project, scene) = CreateProject(effects: false, includeVideo: video);
            using var media = new ProjectMediaSource(project, _folder, new MediaCache(Path.Combine(_folder, ".nmm", "cache")));
            Render(scene, 0, media, 0.5, cancellationToken);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                Render(scene, random.Next(0, 300), media, 0.5, cancellationToken);
            }

            return watch.Elapsed.TotalMilliseconds / count;
        }

        return (Measure(false), HasVideo ? Measure(true) : double.NaN);
    }

    private BgraImage Render(Scene scene, long frame, IMediaSource media, double scale, CancellationToken cancellationToken) =>
        _backend.Render(TimelineEvaluator.Evaluate(scene, frame), media, scale, cancellationToken);

    private (Project Project, Scene Scene) CreateProject(bool effects, bool includeVideo = true)
    {
        var project = Project.CreateNew("ベンチマーク", new SceneFormat(Width, Height, 30, 48000), 8);
        var scene = project.Root;
        var layers = scene.Layers.Select(layer => layer.Id).ToArray();
        var assets = ImmutableDictionary.CreateBuilder<AssetId, Asset>();

        AssetId Add(string path, AssetKind kind)
        {
            var asset = new Asset(AssetId.New(), kind, path);
            assets.Add(asset.Id, asset);
            return asset.Id;
        }

        const long length = 600;
        var blur = effects ? new[] { Effect(BuiltInEffects.Blur, ("radius", 12.0)) } : [];
        var adjust = effects ? new[] { Effect(BuiltInEffects.ColorAdjust, ("brightness", 10.0), ("contrast", 20.0), ("saturation", 30.0)) } : [];
        var moving = Animatable.FromKeys(new Keyframe<double>(0, -400), new Keyframe<double>(length, 400));

        var items = new List<Item>
        {
            new ImageItem(ItemId.New(), layers[0], 0, length, Add("background.png", AssetKind.Image)) { Effects = [.. adjust] },
            new ImageItem(ItemId.New(), layers[2], 0, length, Add("photo.png", AssetKind.Image))
            {
                Transform = Transform2D.Identity with { ScaleX = 0.25, ScaleY = 0.25, Rotation = 8.0, X = -500.0, Y = -200.0 },
                Effects = [.. blur],
            },
            new ImageItem(ItemId.New(), layers[3], 0, length, Add("sprite.png", AssetKind.Image))
            {
                Transform = Transform2D.Identity with { X = moving, Y = 150.0 },
                Effects = [.. blur, .. adjust],
            },
            new ShapeItem(ItemId.New(), layers[4], 0, length, new ShapeStyle(
                ShapeKind.Rectangle, 1600, 220, new LinearGradientPaint(new RgbaColor(0, 0, 0, 200), new RgbaColor(40, 40, 80, 200)), RgbaColor.White, 4, 24))
            {
                Transform = Transform2D.Identity with { Y = 380.0 },
            },
            new TextItem(ItemId.New(), layers[5], 0, length, "ゆっくり解説の字幕です。\n2 行目もあります")
            {
                Style = TextStyle.Default with { FontSize = 56, Shadow = new TextShadow(new RgbaColor(0, 0, 0, 160), 4, 4, 3) },
                Transform = Transform2D.Identity with { Y = 380.0 },
            },
            new TextItem(ItemId.New(), layers[6], 0, length, "性能スパイク")
            {
                Style = TextStyle.Default with { FontSize = 96, Bold = true, Outline = new TextOutline(new RgbaColor(200, 40, 80), 8) },
                Transform = Transform2D.Identity with { Y = -380.0, Rotation = Animatable.FromKeys(new Keyframe<double>(0, -3), new Keyframe<double>(length, 3)) },
            },
            new ImageItem(ItemId.New(), layers[7], 0, length, Add("character.png", AssetKind.Image))
            {
                Transform = Transform2D.Identity with { X = 650.0, Y = 100.0 },
            },
        };

        if (includeVideo && _video is not null)
        {
            items.Add(new VideoItem(ItemId.New(), layers[1], 0, length, Add(_video, AssetKind.Video))
            {
                Transform = Transform2D.Identity with { ScaleX = 0.6, ScaleY = 0.6, X = 250.0, Y = -60.0 },
            });
        }

        scene = scene with { Items = items.ToImmutableDictionary(item => item.Id) };
        project = project.WithScene(scene) with { Assets = assets.ToImmutable() };
        return (project, scene);
    }

    private static EffectInstance Effect(EffectDefinition definition, params (string Name, double Value)[] values) =>
        new(EffectId.New(), definition.Type, values.ToImmutableDictionary(value => value.Name, value => (ParameterValue)new NumberParameter(value.Value)));

    // 素材の画像を作る（写真の代わりに、細かい模様の入った大きな画像）
    private void CreateAssets()
    {
        CreatePng("background.png", Width, Height, canvas =>
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Width, Height), [new SKColor(30, 60, 120), new SKColor(200, 120, 60)], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, Width, Height, paint);
        });
        CreatePng("photo.png", 4000, 3000, canvas =>
        {
            var random = new Random(42);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.Clear(new SKColor(240, 230, 210));
            for (var i = 0; i < 3000; i++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawCircle(random.Next(4000), random.Next(3000), random.Next(5, 120), paint);
            }
        });
        CreatePng("sprite.png", 800, 600, canvas =>
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(400, 300), 300, [new SKColor(255, 255, 255, 255), new SKColor(80, 200, 255, 0)], SKShaderTileMode.Clamp),
            };
            canvas.DrawOval(new SKRect(100, 0, 700, 600), paint);
        });
        CreatePng("character.png", 600, 900, canvas =>
        {
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(250, 220, 200) };
            canvas.DrawCircle(300, 220, 180, paint);
            paint.Color = new SKColor(200, 40, 60);
            canvas.DrawRoundRect(new SKRect(120, 380, 480, 900), 60, 60, paint);
        });
    }

    private void CreatePng(string name, int width, int height, Action<SKCanvas> draw)
    {
        var path = Path.Combine(_folder, name);
        if (File.Exists(path))
        {
            return;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            draw(canvas);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
