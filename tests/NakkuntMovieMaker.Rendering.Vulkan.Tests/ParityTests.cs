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

    public static TheoryData<string> Scenes => new(RenderScenes.Names);

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
        using var scene = RenderScenes.Create(name);
        if (scene.RequiresFonts)
        {
            Fonts.RequireOrSkip();
        }

        var expected = _cpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);
        var actual = gpu.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);

        var difference = ImageCompare.Compare(expected, actual);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {difference}");
        Assert.True(difference.Psnr >= Tolerance(name), $"{name}: CPU 版と違います（{difference}、基準は PSNR {Tolerance(name)}dB 以上）");
    }

    // 場面ごとの基準（PSNR、dB）。M2 の手順 D で GPU に移すときに見直す
    private static double Tolerance(string name) => 45;

    private static VulkanBackend SkipOrFail()
    {
        using var device = Gpu.CreateOrSkip();
        throw new InvalidOperationException("GPU のデバイスは作れたが、描画の準備ができませんでした");
    }
}
