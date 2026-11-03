using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using NakkuntMovieMaker.App.Controls;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>
/// アイテムが 3,000 個あるタイムラインを、横にスクロールしながら描く時間（目標は 60fps = 16.7ms 以内）。
/// テストを動かす PC で速さが違うので、ここでは 50ms を超えないことだけを確かめ、測った値を出力する
/// </summary>
public sealed class TimelinePerformanceTests : IDisposable
{
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public Task ScrollsThroughThreeThousandItems() => Ui.Run(() =>
    {
        using var session = _environment.CreateSession();
        var layers = session.Scene.Layers;
        var items = ImmutableDictionary.CreateBuilder<ItemId, Item>();
        for (var i = 0; i < 3000; i++)
        {
            var layer = layers[i % layers.Count].Id;
            Item item = (i % 3) switch
            {
                0 => new TextItem(ItemId.New(), layer, i / layers.Count * 40L, 35, $"テキスト {i}"),
                1 => new ShapeItem(ItemId.New(), layer, i / layers.Count * 40L, 35, new ShapeStyle(ShapeKind.Rectangle, 10, 10, new SolidPaint(RgbaColor.White))),
                _ => new VoiceItem(ItemId.New(), layer, i / layers.Count * 40L, 35, CharacterId.New(), $"セリフ {i}"),
            };
            items.Add(item.Id, item);
        }

        session.Execute(new ReplaceScene(session.Scene with { Items = items.ToImmutable() }));
        var timeline = new TimelineViewModel(session, new FakeDialogs()) { PixelsPerFrame = 4 };
        var control = new TimelineControl { ViewModel = timeline };
        var size = new Size(1600, 400);
        control.Measure(size);
        control.Arrange(new Rect(size));
        using var target = new RenderTargetBitmap(new PixelSize(1600, 400));

        target.Render(control); // 準備
        var watch = Stopwatch.StartNew();
        const int Steps = 60;
        for (var step = 0; step < Steps; step++)
        {
            timeline.ScrollX = step * 200;
            target.Render(control);
        }

        var perFrame = watch.Elapsed.TotalMilliseconds / Steps;
        TestContext.Current.TestOutputHelper?.WriteLine($"アイテム 3,000 個のタイムラインの 1 回の描画：{perFrame:0.0} ms");
        Assert.True(perFrame < 50, $"1 回の描画に {perFrame:0.0} ms かかりました");
    });
}
