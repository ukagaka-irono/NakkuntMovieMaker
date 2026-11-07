using NakkuntMovieMaker.Rendering.Skia;

namespace NakkuntMovieMaker.Rendering.Tests;

/// <summary>
/// CPU 版の描画が、ゴールデン画像（Golden/*.png）と同じになるか。場面は RenderScenes にあり、GPU 版の比較でも同じ場面を使う
/// </summary>
public sealed class GoldenImageTests
{
    private readonly SkiaCpuBackend _backend = new();

    public static TheoryData<string> Scenes => new(RenderScenes.Names);

    public static TheoryData<string> LinearScenes => new(RenderScenes.LinearNames);

    [Theory]
    [MemberData(nameof(Scenes))]
    public void MatchesTheGoldenImage(string name)
    {
        using var scene = RenderScenes.Create(name);
        if (scene.RequiresFonts)
        {
            Fonts.RequireOrSkip();
        }

        var image = _backend.Render(scene.Plan, scene.Media, scene.Scale, TestContext.Current.CancellationToken);

        Golden.AssertMatches(image, scene.Name);
    }

    [Theory]
    [MemberData(nameof(LinearScenes))]
    public void MatchesTheLinearGoldenImage(string name) => MatchesTheGoldenImage(name);

    [Fact]
    public void DefaultColorDrawsLikeM1()
    {
        // 色の設定が既定値なら、M1 と同じ（8 ビットの sRGB の画像に直接描く）
        using var scene = RenderScenes.Create("composition");
        Assert.Equal(FrameColor.Default, scene.Plan.Color);
        Assert.False(scene.Plan.Color.IsLinear);
    }

    private static string GoldenFolder([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Golden");

    [Fact]
    public void HasEverySceneOfM1()
    {
        // M1 のゴールデン画像は 28 枚（合成 2、図形 1、文字 3、エフェクト 15、トランジション 7）。どれにも場面がある
        Assert.Equal(28, RenderScenes.Names.Count);
        var goldens = Directory.GetFiles(GoldenFolder(), "*.png").Select(path => Path.GetFileNameWithoutExtension(path)!).Order().ToList();
        Assert.Equal(goldens, RenderScenes.Names.Order().ToList());
        Assert.Equal(RenderScenes.Names.Count, RenderScenes.Names.Distinct().Count());
    }
}
