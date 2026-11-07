using System.Numerics;
using NakkuntMovieMaker.Rendering.Color;

namespace NakkuntMovieMaker.Rendering.Tests;

/// <summary>描画の方式に共通の計算（GPU のシェーダーは、これと同じ結果になるように書く）</summary>
public sealed class RenderMathTests
{
    [Fact]
    public void HashIsDeterministicAndInRange()
    {
        // 値を固定しておく（GPU のシェーダーと比べる基準になる）
        Assert.Equal(RenderMath.Hash(7, 10, 20, 0), RenderMath.Hash(7, 10, 20, 0));
        Assert.NotEqual(RenderMath.Hash(7, 10, 20, 0), RenderMath.Hash(7, 10, 20, 1));
        Assert.NotEqual(RenderMath.Hash(7, 10, 20, 0), RenderMath.Hash(8, 10, 20, 0));
        var values = Enumerable.Range(-50, 100).SelectMany(x => Enumerable.Range(-50, 100).Select(y => RenderMath.Hash(1, x, y, 0))).ToList();
        Assert.All(values, value => Assert.InRange(value, 0f, 0.99999994f));
        Assert.InRange(values.Average(), 0.49, 0.51);
    }

    [Theory]
    [InlineData(0.2f, 0.4f, 0.1f, 0f)]
    [InlineData(0.2f, 0.4f, 0.3f, 0.5f)]
    [InlineData(0.2f, 0.4f, 0.5f, 1f)]
    [InlineData(0.3f, 0.3f, 0.2f, 0f)] // 幅が 0 なら段差
    [InlineData(0.3f, 0.3f, 0.3f, 1f)]
    public void SmoothStepMatchesTheUsualDefinition(float edge0, float edge1, float x, float expected) =>
        Assert.Equal(expected, RenderMath.SmoothStep(edge0, edge1, x), 5);

    [Fact]
    public void BlurSizes()
    {
        Assert.Equal(5f, RenderMath.BlurSigma(30, 0.5f));
        Assert.Equal(15, RenderMath.BlurMargin(5f));
        Assert.Equal(0, RenderMath.BlurMargin(0f));
    }

    [Theory]
    [InlineData("fromLeft", -1, 0)]
    [InlineData("fromRight", 1, 0)]
    [InlineData("fromTop", 0, -1)]
    [InlineData("fromBottom", 0, 1)]
    public void TransitionDirections(string name, float x, float y) => Assert.Equal(new Vector2(x, y), RenderMath.TransitionDirection(name));

    [Fact]
    public void CubeLutExposesItsTableForTheGpu()
    {
        // 2×2×2 の LUT：赤と青を入れ替える
        string[] lines =
        [
            "LUT_3D_SIZE 2",
            "0 0 0", "0 0 1", "0 1 0", "0 1 1",
            "1 0 0", "1 0 1", "1 1 0", "1 1 1",
        ];
        var lut = CubeLut.TryParse(lines)!;

        Assert.Equal(2, lut.Size);
        Assert.Equal(8, lut.Table.Length);
        Assert.Equal((Vector3.Zero, Vector3.One), (lut.DomainMin, lut.DomainScale));
        Assert.Equal(new Vector3(0, 0, 1), lut.Sample(new Vector3(1, 0, 0)));
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), lut.Sample(new Vector3(0.75f, 0.5f, 0.25f)));
    }
}
