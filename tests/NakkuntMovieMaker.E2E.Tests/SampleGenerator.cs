using System.Collections.Immutable;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Voice;
using NakkuntMovieMaker.Voice.ColorfulTalk;
using SkiaSharp;

namespace NakkuntMovieMaker.E2E.Tests;

/// <summary>
/// samples/ の 2 つのサンプルプロジェクトを作る。素材（画像・動画・BGM・声・立ち絵）はすべてここで作るので、著作権の心配がない。
/// 声は ColorfulTalk（同梱の音声合成）で作る。
/// サンプルを作り直すときだけ、NMM_GENERATE_SAMPLES=1 を付けて実行する
/// </summary>
public sealed class SampleGenerator
{
    private const int SampleRate = 48000;
    private static readonly SceneFormat Format = SceneFormat.Landscape1080p30;

    [Fact]
    public void Generate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NMM_GENERATE_SAMPLES") == "1", "サンプルを作り直すときだけ実行します（NMM_GENERATE_SAMPLES=1）");
        Samples.RequireFfmpeg();
        GenerateGeneral(Samples.Folder("general"));
        GenerateYukkuri(Samples.Folder("yukkuri"));
    }

    // 一般的な動画編集のサンプル：背景の画像、動画、ロゴ、図形、文字のアニメーション、エフェクト、トランジション、BGM（10 秒）
    private static void GenerateGeneral(string folder)
    {
        Reset(folder);
        var media = Directory.CreateDirectory(Path.Combine(folder, "media")).FullName;
        SavePng(Path.Combine(media, "background.png"), 1920, 1080, canvas =>
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1920, 1080), [new SKColor(24, 40, 82), new SKColor(120, 46, 110)], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, 1920, 1080, paint);
            using var dots = new SKPaint { Color = new SKColor(255, 255, 255, 28), IsAntialias = true };
            for (var i = 0; i < 40; i++)
            {
                canvas.DrawCircle(48 * i % 1920, (i * 197) % 1080, 6 + i % 5 * 4, dots);
            }
        });
        SavePng(Path.Combine(media, "logo.png"), 512, 512, canvas =>
        {
            using var ring = new SKPaint { Color = new SKColor(255, 210, 74), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 36 };
            using var fill = new SKPaint { Color = new SKColor(74, 210, 255), IsAntialias = true };
            canvas.DrawCircle(256, 256, 200, fill);
            canvas.DrawCircle(256, 256, 200, ring);
            using var play = new SKPath();
            play.MoveTo(200, 160);
            play.LineTo(360, 256);
            play.LineTo(200, 352);
            play.Close();
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawPath(play, white);
        });
        CreateClip(Path.Combine(media, "clip.mp4"));
        WavWriter.WriteFile(Path.Combine(media, "bgm.wav"), Bgm(10), 2, SampleRate);

        var project = Project.CreateNew("一般的な動画編集のサンプル", Format, 5);
        var scene = project.Root;
        LayerId Layer(int index) => scene.Layers[index].Id;
        Asset Add(AssetKind kind, string path)
        {
            var asset = new Asset(AssetId.New(), kind, path);
            project = project with { Assets = project.Assets.Add(asset.Id, asset) };
            return asset;
        }

        var background = Add(AssetKind.Image, "media/background.png");
        var logo = Add(AssetKind.Image, "media/logo.png");
        var clip = Add(AssetKind.Video, "media/clip.mp4");
        var bgm = Add(AssetKind.Audio, "media/bgm.wav");
        EffectInstance Effect(string type, params (string Name, ParameterValue Value)[] parameters) =>
            new(EffectId.New(), type, parameters.ToImmutableDictionary(parameter => parameter.Name, parameter => parameter.Value));

        Item[] items =
        [
            new ImageItem(ItemId.New(), Layer(0), 0, 300, background.Id)
            {
                Effects = [Effect("nmm.colorAdjust", ("saturation", new NumberParameter(20.0)))],
            },
            new VideoItem(ItemId.New(), Layer(1), 0, 90, clip.Id)
            {
                Transform = Transform2D.Identity with { ScaleX = 1.2, ScaleY = 1.2 },
                Audio = AudioMix.Default with { Volume = 0.0 },
            },
            new ImageItem(ItemId.New(), Layer(1), 90, 105, logo.Id)
            {
                Transition = new TransitionInstance("nmm.crossfade", 20),
                Transform = Transform2D.Identity with
                {
                    Rotation = Animatable<double>.FromKeys([new Keyframe<double>(0, -20, new Easing(EasingCurve.Back, EasingMode.Out)), new Keyframe<double>(40, 0)]),
                },
                Effects = [Effect("nmm.dropShadow", ("offsetX", new NumberParameter(16.0)), ("offsetY", new NumberParameter(16.0)), ("blur", new NumberParameter(12.0)))],
            },
            new ShapeItem(ItemId.New(), Layer(1), 195, 105, new ShapeStyle(ShapeKind.Star, 420, 420, new LinearGradientPaint(new RgbaColor(255, 210, 74), new RgbaColor(255, 90, 78), 45), RgbaColor.White, 8))
            {
                Transition = new TransitionInstance("nmm.wipe", 20),
                Transform = Transform2D.Identity with { Rotation = Animatable<double>.FromKeys([new Keyframe<double>(0, 0), new Keyframe<double>(104, 180)]) },
            },
            new TextItem(ItemId.New(), Layer(2), 0, 150, "NakkuntMovieMaker")
            {
                Style = TextStyle.Default with { FontSize = 120, Bold = true, Outline = new TextOutline(new RgbaColor(24, 40, 82), 8) },
                Transform = Transform2D.Identity with
                {
                    Y = Animatable<double>.FromKeys([new Keyframe<double>(0, 420, new Easing(EasingCurve.Cubic, EasingMode.Out)), new Keyframe<double>(30, 360)]),
                },
                Opacity = Animatable<double>.FromKeys([new Keyframe<double>(0, 0), new Keyframe<double>(20, 1), new Keyframe<double>(130, 1), new Keyframe<double>(149, 0)]),
                Effects = [Effect("nmm.glow", ("radius", new NumberParameter(16.0)), ("strength", new NumberParameter(60.0)))],
            },
            new TextItem(ItemId.New(), Layer(2), 150, 150, "サンプル動画")
            {
                Style = TextStyle.Default with { FontSize = 96, Vertical = true, RevealCharsPerSecond = 8, Shadow = new TextShadow(new RgbaColor(0, 0, 0, 160), 6, 6, 6) },
                Transform = Transform2D.Identity with { X = -700 },
            },
            new AudioItem(ItemId.New(), Layer(4), 0, 300, bgm.Id) { Audio = AudioMix.Default with { Volume = 0.6, FadeInFrames = 15, FadeOutFrames = 45 } },
        ];
        foreach (var item in items)
        {
            scene = scene with { Items = scene.Items.Add(item.Id, item) };
        }

        var marker = new Marker(MarkerId.New(), 90, "ロゴ", new RgbaColor(74, 210, 255));
        scene = scene with { Markers = [marker] };
        project = project.WithScene(scene);
        new ProjectStore().Save(project, Path.Combine(folder, "general.nmmproj"));
    }

    // ゆっくり解説のサンプル：2 人のキャラクター（パーツ式の立ち絵・口パク・目パチ）、セリフと字幕、BGM（ダッキング）
    private static void GenerateYukkuri(string folder)
    {
        Reset(folder);
        var media = Directory.CreateDirectory(Path.Combine(folder, "media")).FullName;
        CreateTachie(Path.Combine(folder, "tachie", "reimu"), new SKColor(220, 60, 70));
        CreateTachie(Path.Combine(folder, "tachie", "marisa"), new SKColor(250, 200, 60));
        WavWriter.WriteFile(Path.Combine(media, "bgm.wav"), Bgm(12), 2, SampleRate);

        var project = Project.CreateNew("ゆっくり解説のサンプル", Format, 6);
        var scene = project.Root;
        var reimu = new Character(
            CharacterId.New(), "霊夢", new VoiceSpec(ColorfulTalkEngine.EngineId, "f1", VoiceParams.Default with { Pitch = 1.1 }),
            TextStyle.Default with { FontSize = 64, Outline = new TextOutline(new RgbaColor(200, 30, 50), 8) },
            new TachieSpec(TachieKind.Parts, "tachie/reimu"));
        var marisa = new Character(
            CharacterId.New(), "魔理沙", new VoiceSpec(ColorfulTalkEngine.EngineId, "f1", VoiceParams.Default with { Pitch = 0.9, Speed = 1.05 }),
            TextStyle.Default with { FontSize = 64, Outline = new TextOutline(new RgbaColor(200, 150, 20), 8) },
            new TachieSpec(TachieKind.Parts, "tachie/marisa"));
        project = project with { Characters = project.Characters.Add(reimu.Id, reimu).Add(marisa.Id, marisa) };
        var bgm = new Asset(AssetId.New(), AssetKind.Audio, "media/bgm.wav");
        project = project with { Assets = project.Assets.Add(bgm.Id, bgm) };

        // 声は ColorfulTalk で作る。漢字は読めないので、読み（記号列）を付ける
        (Character Who, string Text, string Reading)[] script =
        [
            (reimu, "ゆっくり霊夢です", "ゆっくり/れ'いむです。"),
            (marisa, "ゆっくり魔理沙だぜ", "ゆっくり/まりさだぜ。"),
            (reimu, "今日は動画の作り方を解説するよ", "きょ'うわ/どうがの/つくりか'たお/かいせつするよ。"),
            (marisa, "最後まで見ていってくれよな", "さ'いごまで/み'ていって/くれよな。"),
        ];
        using var engines = new VoiceEngineRegistry([new ColorfulTalkEngine()]);
        using var service = new VoiceService(engines, new VoiceCache(folder));

        var start = 15L;
        var items = new List<Item>
        {
            new ShapeItem(ItemId.New(), scene.Layers[0].Id, 0, 360, new ShapeStyle(ShapeKind.Background, 1920, 1080, new LinearGradientPaint(new RgbaColor(230, 240, 250), new RgbaColor(170, 200, 230), 90))),
            new TachieItem(ItemId.New(), scene.Layers[1].Id, 0, 360, reimu.Id) { Transform = Transform2D.Identity with { X = 600, Y = 120, ScaleX = 1.2, ScaleY = 1.2 } },
            new TachieItem(ItemId.New(), scene.Layers[2].Id, 0, 360, marisa.Id) { Transform = Transform2D.Identity with { X = -600, Y = 120, ScaleX = 1.2, ScaleY = 1.2, FlipHorizontal = true } },
            new AudioItem(ItemId.New(), scene.Layers[5].Id, 0, 360, bgm.Id) { Audio = AudioMix.Default with { Volume = 0.35, FadeOutFrames = 30, DuckingDb = 12 } },
        };
        for (var i = 0; i < script.Length; i++)
        {
            var (who, text, reading) = script[i];
            var voice = service.GetAsync(who.Voice, text, reading).GetAwaiter().GetResult();
            var length = VoiceEditing.LengthFor(voice.Duration, Format.Fps);
            items.Add(new VoiceItem(ItemId.New(), scene.Layers[3].Id, start, length, who.Id, text)
            {
                Reading = reading,
                Source = new SynthesizedVoice(voice.CacheKey),
                Transform = Transform2D.Identity with { Y = Format.Height * 0.38 },
            });
            start += length + 12;
        }

        foreach (var item in items)
        {
            scene = scene with { Items = scene.Items.Add(item.Id, item) };
        }

        project = project.WithScene(scene);
        new ProjectStore().Save(project, Path.Combine(folder, "yukkuri.nmmproj"));
    }

    private static void Reset(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
    }

    private static void SavePng(string path, int width, int height, Action<SKCanvas> draw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    // 3 秒・1280×720 の動画（動く図形）を、このソフトの書き出しで作る
    private static void CreateClip(string path)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nmm-samples", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = Project.CreateNew("clip", new SceneFormat(1280, 720, 30, 48000), 2);
            var scene = project.Root;
            var background = new ShapeItem(ItemId.New(), scene.Layers[0].Id, 0, 90, new ShapeStyle(ShapeKind.Background, 1280, 720, new LinearGradientPaint(new RgbaColor(20, 120, 90), new RgbaColor(10, 40, 60), 30)));
            var ball = new ShapeItem(ItemId.New(), scene.Layers[1].Id, 0, 90, new ShapeStyle(ShapeKind.Ellipse, 160, 160, new SolidPaint(new RgbaColor(255, 240, 200))))
            {
                Transform = Transform2D.Identity with
                {
                    X = Animatable<double>.FromKeys([new Keyframe<double>(0, -500), new Keyframe<double>(89, 500)]),
                    Y = Animatable<double>.FromKeys([new Keyframe<double>(0, 0, new Easing(EasingCurve.Bounce, EasingMode.Out)), new Keyframe<double>(45, 200), new Keyframe<double>(89, 0)]),
                },
            };
            project = project.WithScene(scene with { Items = scene.Items.Add(background.Id, background).Add(ball.Id, ball) });
            using var pipeline = new ExportPipeline(project, project.RootScene, folder);
            pipeline.ExportVideoAsync(new ExportOptions(path, VideoBitrateKbps: 4000, Encoder: EncoderPreference.Software)).GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // 和音を順に鳴らす BGM（ステレオ）
    private static float[] Bgm(int seconds)
    {
        double[][] chords = [[261.6, 329.6, 392.0], [220.0, 261.6, 329.6], [174.6, 220.0, 261.6], [196.0, 246.9, 293.7]];
        var frames = seconds * SampleRate;
        var stereo = new float[frames * 2];
        var beat = SampleRate / 2;
        for (var i = 0; i < frames; i++)
        {
            var chord = chords[i / (beat * 4) % chords.Length];
            var t = (double)i / SampleRate;
            var local = i % beat / (double)beat;
            var envelope = Math.Exp(-3 * local) * 0.6 + 0.15;
            var value = chord.Sum(frequency => Math.Sin(2 * Math.PI * frequency * t) + 0.3 * Math.Sin(4 * Math.PI * frequency * t)) / chord.Length * 0.25 * envelope;
            stereo[i * 2] = (float)(value * (0.9 + 0.1 * Math.Sin(t)));
            stereo[i * 2 + 1] = (float)(value * (0.9 - 0.1 * Math.Sin(t)));
        }

        return stereo;
    }

    // パーツ式の立ち絵（体・目 3 枚・口 3 枚）。YMM4 の「動く立ち絵」と同じフォルダ構成
    private static void CreateTachie(string folder, SKColor color)
    {
        const int Size = 600;
        void Part(string part, string name, Action<SKCanvas> draw) => SavePng(Path.Combine(folder, part, name), Size, Size, draw);

        Part("体", "body.png", canvas =>
        {
            using var body = new SKPaint { Color = color, IsAntialias = true };
            using var face = new SKPaint { Color = new SKColor(255, 230, 210), IsAntialias = true };
            using var outline = new SKPaint { Color = new SKColor(60, 40, 40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };
            canvas.DrawOval(300, 540, 190, 60, body);
            canvas.DrawCircle(300, 280, 200, face);
            canvas.DrawCircle(300, 280, 200, outline);
            using var hair = new SKPaint { Color = color.WithAlpha(230), IsAntialias = true };
            canvas.DrawArc(new SKRect(90, 60, 510, 420), 180, 180, true, hair);
        });

        // 目：閉じた（.0）・半分（.1）・開いた（名前だけ）
        for (var frame = 0; frame < 3; frame++)
        {
            var open = frame / 2.0;
            Part("目", frame == 2 ? "eyes.png" : $"eyes.{frame}.png", canvas =>
            {
                using var paint = new SKPaint { Color = new SKColor(40, 30, 40), IsAntialias = true, StrokeWidth = 8, Style = open == 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill };
                foreach (var x in new[] { 220, 380 })
                {
                    if (open == 0)
                    {
                        canvas.DrawLine(x - 30, 290, x + 30, 290, paint);
                    }
                    else
                    {
                        canvas.DrawOval(x, 290, 26, (float)(40 * open), paint);
                    }
                }
            });
        }

        // 口：閉じた（.0）・半分（.1）・開いた（名前だけ）
        for (var frame = 0; frame < 3; frame++)
        {
            var open = frame / 2.0;
            Part("口", frame == 2 ? "mouth.png" : $"mouth.{frame}.png", canvas =>
            {
                using var paint = new SKPaint { Color = new SKColor(150, 40, 50), IsAntialias = true, StrokeWidth = 6, Style = open == 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill };
                if (open == 0)
                {
                    canvas.DrawLine(270, 390, 330, 390, paint);
                }
                else
                {
                    canvas.DrawOval(300, 390, 34, (float)(30 * open), paint);
                }
            });
        }
    }
}
