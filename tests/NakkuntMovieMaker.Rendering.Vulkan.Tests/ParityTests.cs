using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>
/// GPU 版の描画を、同じ場面を CPU 版（基準）で描いた結果と比べる（ゴールデン画像の 28 の場面）。
/// 同じプロセスで描くので、OS ごとの文字の違いは打ち消し合う
/// </summary>
public sealed class ParityTests : IDisposable
{
    private readonly VulkanDevice? _device;
    private readonly VulkanBackend? _gpu;
    private readonly SkiaCpuBackend _cpu = new();

    public ParityTests()
    {
        try
        {
            _device = VulkanDevice.Create(Gpu.Options);
            _gpu = new VulkanBackend(_device);
        }
        catch (VulkanException)
        {
            // 使えなければ、テストの中でスキップする
        }
    }

    public static TheoryData<string> Scenes => new(RenderScenes.Names.Concat(RenderScenes.LinearNames).Concat(RenderScenes.OutputNames));

    public void Dispose()
    {
        _gpu?.Dispose();
        _device?.Dispose();
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void MatchesTheCpuRenderer(string name)
    {
        var gpu = _gpu ?? SkipOrFail();
        Gpu.RequireRenderer();
        using var scene = RenderScenes.Create(name);
        if (scene.RequiresFonts)
        {
            Fonts.RequireOrSkip();
        }

        var expected = _cpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);
        var effectNodes = gpu.GpuEffectNodes;
        var transitionNodes = gpu.GpuTransitionNodes;
        var actual = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);

        // GPU 版があるエフェクトは、本当に GPU でかけたことも確かめる
        if (name.StartsWith("effect-", StringComparison.Ordinal) && GpuEffects.Find("nmm." + name["effect-".Length..]) is not null)
        {
            Assert.True(gpu.GpuEffectNodes > effectNodes, $"{name}: エフェクトを GPU でかけていません");
        }

        if (name.StartsWith("transition-", StringComparison.Ordinal))
        {
            Assert.True(gpu.GpuTransitionNodes > transitionNodes, $"{name}: トランジションを GPU で描いていません");
        }

        // 調べるとき：NMM_PARITY_DUMP にフォルダーを指定すると、両方の画像を PNG で書き出す
        if (Environment.GetEnvironmentVariable("NMM_PARITY_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            Golden.Save(expected, Path.Combine(dump, name + ".cpu.png"));
            Golden.Save(actual, Path.Combine(dump, name + ".gpu.png"));
        }

        var difference = ImageCompare.Compare(expected, actual);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {difference}");
        var tolerance = Tolerance(name, gpu.UsesSkiaGpu);
        Assert.True(difference.Psnr >= tolerance, $"{name}: CPU 版と違います（{difference}、基準は PSNR {tolerance}dB 以上）");
    }

    [Theory]
    [InlineData("text-horizontal")]
    [InlineData("shapes")]
    [InlineData("linear/shapes")]
    public void DrawsTextAndShapesWithSkiaOnTheGpu(string name)
    {
        // 文字と図形は Skia の GPU 版で描き（CPU で描いて送らない）、動かないものは 2 回目に描き直さない。検証レイヤーのエラーもない
        var gpu = _gpu ?? SkipOrFail();
        Gpu.RequireRenderer();
        Assert.SkipUnless(gpu.UsesSkiaGpu, $"Skia の GPU 版を使わないためスキップします（{gpu.SkiaGpuProblem}）");
        using var scene = RenderScenes.Create(name);
        if (scene.RequiresFonts)
        {
            Fonts.RequireOrSkip();
        }

        VulkanDevice.ClearValidationMessages();
        var (nodes, cpuLayers) = (gpu.GpuSkiaNodes, gpu.CpuLayers);
        var first = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);
        Assert.True(gpu.GpuSkiaNodes > nodes, $"{name}: Skia の GPU 版で描いていません");
        Assert.Equal(cpuLayers, gpu.CpuLayers);

        var draws = gpu.SkiaDraws;
        var second = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);
        Assert.Equal(draws, gpu.SkiaDraws);
        Assert.Equal(first.Pixels, second.Pixels);
        Assert.DoesNotContain(VulkanDevice.ValidationMessages, message => message.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void DrawsTextAndShapesOnTheCpuWhenSkiaGpuIsOff()
    {
        // 切ったときは、これまでどおり CPU で描いて送る
        using var device = Gpu.CreateOrSkip();
        Gpu.RequireRenderer();
        using var gpu = new VulkanBackend(device, gpuVectors: false);
        using var scene = RenderScenes.Create("shapes");
        var cpuLayers = gpu.CpuLayers;

        gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);

        Assert.False(gpu.UsesSkiaGpu);
        Assert.Equal(0, gpu.GpuSkiaNodes);
        Assert.True(gpu.CpuLayers > cpuLayers);
    }

    [Fact]
    public void ReusesEffectResultsForTheSameStill()
    {
        var gpu = _gpu ?? SkipOrFail();
        Gpu.RequireRenderer();
        using var scene = RenderScenes.Create("effect-colorAdjust");

        // 2 回目は、覚えておいたエフェクトの結果を使う（CPU で描き直さない）。結果は同じ
        var first = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);
        var second = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);

        Assert.Equal(first.Pixels, second.Pixels);
    }

    // 場面ごとの基準（PSNR、dB）。CPU 版と計算の順番や精度が違う（16 ビット浮動小数点で計算し、途中で 8 ビットに丸めない）ので、
    // ぼかしを使うもの・境界を距離から求めるものは緩くする。文字と図形は、Skia の GPU 版で描くと縁のアンチエイリアスが CPU 版と少し違うので緩くする。
    // docs/rendering.md の表と合わせる
    private static double Tolerance(string name, bool skiaGpu) => name switch
    {
        "effect-blur" or "effect-dropShadow" or "effect-glow" or "effect-mask" or "effect-shake" => 38,
        _ when skiaGpu && (name.Contains("text-", StringComparison.Ordinal) || name.Contains("shapes", StringComparison.Ordinal)) => 38,
        "effect-border" => 42,
        _ when name.StartsWith("transition-", StringComparison.Ordinal) => 42,
        _ => 45,
    };

    private static VulkanBackend SkipOrFail()
    {
        using var device = Gpu.CreateOrSkip();
        throw new InvalidOperationException("GPU のデバイスは作れたが、描画の準備ができませんでした");
    }
}
