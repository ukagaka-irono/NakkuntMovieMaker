namespace NakkuntMovieMaker.Rendering.Vulkan.Tests;

/// <summary>Vulkan のデバイスを作る・選ぶ</summary>
public sealed class VulkanDeviceTests
{
    [Fact]
    public void CreatesADeviceWithAGraphicsAndComputeQueue()
    {
        VulkanDevice.ClearValidationMessages();
        using (var device = Gpu.CreateOrSkip())
        {
            Assert.False(string.IsNullOrEmpty(device.Info.Name));
            Assert.True(device.Info.Suitable);
            Assert.True(device.Info.ApiVersion >= new Version(1, 2));
            Assert.NotEqual(0, device.Queue.Handle);
            Assert.NotEqual(0, device.GetProcAddress("vkCreateImage", 0, device.Device.Handle));
            Assert.NotEqual(0, device.GetProcAddress("vkCreateDevice", device.Instance.Handle, 0));
        }

        Assert.Empty(VulkanDevice.ValidationMessages);
    }

    [Fact]
    public void ListsTheChosenDevice()
    {
        using var device = Gpu.CreateOrSkip();

        var devices = VulkanDevice.ListDevices();

        Assert.Contains(devices, info => info.Name == device.Info.Name && info.Suitable);
    }

    [Fact]
    public void ReportsAMissingDevice()
    {
        using (Gpu.CreateOrSkip())
        {
        }

        var error = Assert.Throws<VulkanException>(() => VulkanDevice.Create(Gpu.Options with { DeviceName = "存在しない GPU" }));

        Assert.Contains("存在しない GPU", error.Message);
    }
}
