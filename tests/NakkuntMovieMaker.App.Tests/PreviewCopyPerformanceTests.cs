using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>
/// プレビューの表示のためのコピー（I-40）。1080p の 1 枚を、描いた結果からプレビューのビットマップに写す時間を測る。
/// 60fps（16.7ms）の中で、描画と読み戻しのほかに使える時間は少ないので、写すのは 1 枚 8ms 以内を目標にする。
/// テストを動かす PC で速さが違うので、ここでは極端に遅くないこと（手元は 8ms、CI は 50ms）だけを確かめ、測った値を出力する
/// </summary>
public sealed class PreviewCopyPerformanceTests : IDisposable
{
    private const int Width = 1920;
    private const int Height = 1080;

    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    private static bool OnCi => Environment.GetEnvironmentVariable("CI") is "true" or "1";

    [Fact]
    public Task CopiesA1080pFrameQuickly() => Ui.Run(() =>
    {
        var image = new BgraImage(Width, Height, new byte[Width * Height * 4]);
        using var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Assert.True(Bitmaps.CopyInto(bitmap, image)); // 準備

        const int Frames = 120;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            Bitmaps.CopyInto(bitmap, image);
        }

        var perFrame = watch.Elapsed.TotalMilliseconds / Frames;
        TestContext.Current.TestOutputHelper?.WriteLine($"1080p の 1 枚をプレビューのビットマップに写す時間：{perFrame:0.00} ms");
        Assert.True(perFrame < (OnCi ? 50 : 8), $"1 枚 {perFrame:0.00} ms かかりました");
    });

    [Fact]
    public Task RendersAndShowsA1080pFrame() => Ui.Run(async () =>
    {
        // 描いて（GPU なら読み戻して）写すまでの 1 枚の時間。毎フレーム動く図形があるので、キャッシュは使わない
        using var session = _environment.CreateSession();
        session.NewProject("p", new SceneFormat(Width, Height, 60, 48000));
        var layer = session.Scene.Layers[0].Id;
        var shape = new ShapeItem(ItemId.New(), layer, 0, 600, new ShapeStyle(ShapeKind.Rectangle, 400, 300, new SolidPaint(RgbaColor.White)))
        {
            Transform = Transform2D.Identity with
            {
                X = Animatable<double>.FromKeys([new Keyframe<double>(0, -500), new Keyframe<double>(599, 500)]),
            },
        };
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, shape));
        session.Frames.Scale = 1; // フル解像度で表示する
        using var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var cancellation = TestContext.Current.CancellationToken;
        Bitmaps.CopyInto(bitmap, (await session.Frames.RequestAsync(0, cancellation)).Image); // 準備

        const int Frames = 60;
        var watch = Stopwatch.StartNew();
        for (var frame = 1; frame <= Frames; frame++)
        {
            var rendered = await session.Frames.RequestAsync(frame, cancellation);
            Assert.True(Bitmaps.CopyInto(bitmap, rendered.Image));
        }

        var perFrame = watch.Elapsed.TotalMilliseconds / Frames;
        TestContext.Current.TestOutputHelper?.WriteLine($"{session.RendererName}：1080p の 1 枚を描いて写すまで {perFrame:0.0} ms（1 枚ずつ順に）");
        Assert.True(perFrame < (OnCi ? 500 : 100), $"1 枚 {perFrame:0.0} ms かかりました");
    });
}
