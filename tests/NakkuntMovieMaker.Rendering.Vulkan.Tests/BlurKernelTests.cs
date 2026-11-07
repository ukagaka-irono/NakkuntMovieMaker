namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>GPU のぼかしの重み（CPU 版の Skia と同じ、3 回の箱型のぼかし）</summary>
public sealed class BlurKernelTests
{
    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.65f)]
    public void SmallSigmaDoesNotBlur(float sigma) => Assert.Equal([1f], GpuEffectContext.BlurKernel(sigma));

    [Theory]
    [InlineData(1f, 2)] // 幅 2（偶数）
    [InlineData(1.6f, 3)] // 幅 3（奇数）
    [InlineData(5.3f, 10)]
    public void KernelIsSymmetricAndSumsToOne(float sigma, int window)
    {
        var kernel = GpuEffectContext.BlurKernel(sigma);

        Assert.Equal(window, (int)Math.Floor(sigma * 3 * Math.Sqrt(2 * Math.PI) / 4 + 0.5));
        Assert.Equal(1, kernel.Sum(), 5);
        Assert.Equal(kernel, kernel.Reverse());
        Assert.Equal(window % 2 == 1 ? 3 * (window - 1) + 1 : 3 * window - 1, kernel.Length);
    }

    [Fact]
    public void OddWindowIsThreeCenteredBoxes()
    {
        // 幅 3 の箱を 3 回：重みは (1, 3, 6, 7, 6, 3, 1) / 27
        var kernel = GpuEffectContext.BlurKernel(1.6f);

        Assert.Equal(new[] { 1, 3, 6, 7, 6, 3, 1 }.Select(w => w / 27f), kernel, (a, b) => Math.Abs(a - b) < 1e-6);
    }
}
