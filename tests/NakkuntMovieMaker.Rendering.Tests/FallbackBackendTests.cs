using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Rendering.Tests;

/// <summary>GPU 版で描けなくなったら、CPU 版に切り替える</summary>
public sealed class FallbackBackendTests
{
    [Fact]
    public void SwitchesToTheFallbackOnceTheGpuFails()
    {
        var gpu = new FakeBackend("GPU");
        var cpu = new FakeBackend("CPU");
        using var backend = new FallbackBackend(gpu, cpu, ex => ex is InvalidOperationException);
        var reasons = new List<string>();
        backend.FellBack += reasons.Add;
        var plan = new FramePlan(4, 4, RgbaColor.Black, []);

        Assert.Equal("GPU", backend.Info.Name);
        Assert.Equal(1, backend.Render(plan, new TestMedia(), cancellationToken: TestContext.Current.CancellationToken).Pixels[0]);

        gpu.Fail = true;
        Assert.Equal(2, backend.Render(plan, new TestMedia(), cancellationToken: TestContext.Current.CancellationToken).Pixels[0]); // 失敗したフレームも CPU で描く
        Assert.Equal(2, backend.Render(plan, new TestMedia(), cancellationToken: TestContext.Current.CancellationToken).Pixels[0]);
        Assert.Equal("CPU", backend.Info.Name);
        Assert.True(backend.HasFallenBack);
        Assert.Single(reasons);
        Assert.Contains("CPU で描きます", reasons[0]);
        Assert.Equal(2, gpu.Calls); // 切り替えたあとは GPU を呼ばない
    }

    [Fact]
    public void DoesNotHideOtherErrors()
    {
        var gpu = new FakeBackend("GPU") { Fail = true };
        using var backend = new FallbackBackend(gpu, new FakeBackend("CPU"), ex => ex is NotSupportedException);

        Assert.Throws<InvalidOperationException>(() => backend.Render(new FramePlan(4, 4, RgbaColor.Black, []), new TestMedia(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(backend.HasFallenBack);
    }

    private sealed class FakeBackend(string name) : IRenderBackend
    {
        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public RenderBackendInfo Info { get; } = new(RenderBackendKind.Cpu, name, null, 1);

        public RenderResult Render(FramePlan plan, IMediaSource media, RenderRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail)
            {
                throw new InvalidOperationException("デバイスが失われました");
            }

            var pixels = new byte[plan.Width * plan.Height * 4];
            pixels[0] = name == "GPU" ? (byte)1 : (byte)2;
            return new CpuRenderResult(new BgraImage(plan.Width, plan.Height, pixels));
        }

        public void Dispose()
        {
        }
    }
}
