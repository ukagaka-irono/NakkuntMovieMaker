using System.Numerics;

namespace NakkuntMovieMaker.ColorManagement.Tests;

/// <summary>
/// nmm_ocio が必要なテストの前提。用意していない環境ではスキップする。
/// CI では NMM_REQUIRE_OCIO=1 にして、スキップで失敗を見逃さないようにする
/// </summary>
internal static class OcioRequirement
{
    public static void RequireOrSkip()
    {
        var loaded = Ocio.TryLoad(out var error);
        var required = Environment.GetEnvironmentVariable("NMM_REQUIRE_OCIO") == "1";
        Assert.SkipWhen(!loaded && !required, $"nmm_ocio を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }
}

public sealed class OcioTests
{
    [Fact]
    public void ReportsTheBundledVersionAndBuiltinConfigs()
    {
        OcioRequirement.RequireOrSkip();

        Assert.Equal("2.5.2", Ocio.Version);
        Assert.Contains(Ocio.BuiltinConfigs, config => config.Name == Ocio.DefaultBuiltinConfig);
        Assert.All(Ocio.BuiltinConfigs, config => Assert.Contains("aces", config.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultConfigHasTheWorkingSpacesAndDisplays()
    {
        OcioRequirement.RequireOrSkip();
        using var config = OcioConfig.LoadBuiltin(Ocio.DefaultBuiltinConfig);

        Assert.Equal(Ocio.DefaultBuiltinConfig, config.Name);
        Assert.Equal(WorkingColorSpaces.Srgb, config.CanonicalColorSpace(WorkingColorSpaces.Srgb));
        Assert.Equal(WorkingColorSpaces.Linear, config.CanonicalColorSpace(WorkingColorSpaces.Linear));
        Assert.Equal(WorkingColorSpaces.Srgb, config.CanonicalColorSpace("sRGB - Texture")); // 別名
        Assert.Contains("sRGB - Display", config.Displays);
        Assert.NotEmpty(config.Views(config.DefaultDisplay));
        Assert.Null(config.CanonicalColorSpace("存在しない色空間"));
    }

    [Fact]
    public void ReportsMissingConfigsAndColorSpaces()
    {
        OcioRequirement.RequireOrSkip();

        Assert.Throws<OcioException>(() => OcioConfig.LoadBuiltin("no-such-config"));
        using var config = OcioConfig.LoadBuiltin(Ocio.DefaultBuiltinConfig);
        var error = Assert.Throws<OcioException>(() => config.CreateProcessor("ACEScg", "存在しない色空間"));
        Assert.Contains("存在しない色空間", error.Message);
    }

    [Fact]
    public void ProcessorConvertsSrgbToLinear()
    {
        OcioRequirement.RequireOrSkip();
        using var config = OcioConfig.LoadBuiltin(Ocio.DefaultBuiltinConfig);
        using var processor = config.CreateProcessor(WorkingColorSpaces.Srgb, WorkingColorSpaces.Linear);

        float[] rgb = [0.5f, 1, 0];
        processor.Apply(rgb);

        Assert.False(processor.IsNoOp);
        Assert.False(processor.HasChannelCrosstalk);
        Assert.Equal(0.21404f, rgb[0], 4);
        Assert.Equal(1, rgb[1], 4);
        Assert.Equal(0, rgb[2], 4);
    }

    [Fact]
    public void SameColorSpaceIsNoOp()
    {
        OcioRequirement.RequireOrSkip();
        using var config = OcioConfig.LoadBuiltin(Ocio.DefaultBuiltinConfig);
        using var processor = config.CreateProcessor("ACEScg", "ACEScg");

        Assert.True(processor.IsNoOp);
        Assert.Null(BakedColorLut.Bake(processor));
    }

    [Theory]
    [InlineData("sRGB - Display", "Rec.1886 Rec.709 - Display")]
    [InlineData("sRGB Encoded P3-D65", WorkingColorSpaces.Srgb)]
    [InlineData(WorkingColorSpaces.Srgb, WorkingColorSpaces.Linear)]
    public void BakedLutMatchesOcioForSdr(string source, string destination)
    {
        // 焼いた LUT（シェーパーと 65³）と、OCIO そのものの結果の差が 2/1023 以下
        OcioRequirement.RequireOrSkip();
        using var transforms = new ColorTransforms();
        var lut = transforms.Transform(null, source, destination);
        Assert.NotNull(lut);
        using var processor = transforms.Config(null).CreateProcessor(source, destination);

        AssertClose(lut, rgb => processor.Apply(rgb), 2.0 / 1023);
    }

    [Fact]
    public void BakedToneMapMatchesOcioForPq()
    {
        // PQ（HDR 1000 nits）の動画を、シーンの光に戻してから SDR の sRGB で表示する値にする（1000 nits までの値で確かめる）。
        // ACES 2.0 の変換は、色域の境界の近く（彩度の高い黄色など）で急に曲がるので、65³ の LUT では SDR ほど正確にならない
        // （基準の PC で、99% の色は 27/1023 以内、最大は 108/1023）
        OcioRequirement.RequireOrSkip();
        using var transforms = new ColorTransforms();
        const string Hdr = "ACES 2.0 - HDR 1000 nits (Rec.2020)";
        const string Sdr = "ACES 2.0 - SDR 100 nits (Rec.709)";
        var lut = transforms.ToneMap(null, "Rec.2100-PQ - Display", Hdr, "sRGB - Display", Sdr);
        Assert.NotNull(lut);
        var config = transforms.Config(null);
        using var toScene = config.CreateDisplayViewProcessor("ACEScg", "Rec.2100-PQ - Display", Hdr, inverse: true);
        using var toDisplay = config.CreateDisplayViewProcessor("ACEScg", "sRGB - Display", Sdr);

        AssertClose(lut, rgb =>
        {
            toScene.Apply(rgb);
            toDisplay.Apply(rgb);
        }, 32.0 / 1023, maxTolerance: 128.0 / 1023, maxInput: 0.7518f);
    }

    // 0〜maxInput の色をばらばらに 4096 個選び、LUT と OCIO の結果（どちらも 0〜1 に収めたもの）の差を確かめる。
    // tolerance は 99% の色の差の上限、maxTolerance はすべての色の差の上限
    private static void AssertClose(Rendering.Color.ColorLut lut, Action<float[]> convert, double tolerance, double? maxTolerance = null, float maxInput = 1)
    {
        var random = new Random(1234);
        var samples = Enumerable.Range(0, 4096).Select(_ => new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * maxInput).ToArray();
        var expected = samples.SelectMany(color => new[] { color.X, color.Y, color.Z }).ToArray();
        convert(expected);

        var errors = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            // 出力の範囲の外（色域の外）は、表示で 0〜1 に収めるので、比べるのも収めてから
            var actual = lut.Sample(samples[i]);
            var reference = Vector3.Clamp(new Vector3(expected[i * 3], expected[i * 3 + 1], expected[i * 3 + 2]), Vector3.Zero, Vector3.One);
            var difference = Vector3.Abs(actual - reference);
            errors[i] = Math.Max(difference.X, Math.Max(difference.Y, difference.Z));
        }

        Array.Sort(errors);
        var p99 = errors[(int)(errors.Length * 0.99)];
        var worst = errors[^1];
        TestContext.Current.TestOutputHelper?.WriteLine($"99% の差 {p99 * 1023:F2}/1023、最大の差 {worst * 1023:F2}/1023");
        Assert.True(p99 <= tolerance, $"99% の差 {p99 * 1023:F2}/1023");
        Assert.True(worst <= (maxTolerance ?? tolerance), $"最大の差 {worst * 1023:F2}/1023");
    }

    [Fact]
    public void TransformsAreBakedOnceAndSkippedWhenNothingChanges()
    {
        OcioRequirement.RequireOrSkip();
        using var transforms = new ColorTransforms();

        Assert.Null(transforms.Transform(null, WorkingColorSpaces.Srgb, WorkingColorSpaces.Srgb));
        Assert.Null(transforms.Transform(null, "sRGB - Texture", WorkingColorSpaces.Srgb)); // 別名どうしは OCIO が何もしない変換にする
        var first = transforms.Transform(null, WorkingColorSpaces.Srgb, WorkingColorSpaces.Linear);
        Assert.Same(first, transforms.Transform(null, WorkingColorSpaces.Srgb, WorkingColorSpaces.Linear));
        Assert.Throws<OcioException>(() => transforms.Transform(null, WorkingColorSpaces.Srgb, "存在しない色空間"));
    }
}
