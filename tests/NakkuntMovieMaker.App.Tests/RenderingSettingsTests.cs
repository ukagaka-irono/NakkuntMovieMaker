using Avalonia.Controls;
using Avalonia.LogicalTree;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.App.Views;
using NakkuntMovieMaker.App.Views.Dialogs;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>設定の「描画」（方式・GPU・メモリの上限）と、状態バーの描画の表示（I-37）</summary>
public sealed class RenderingSettingsTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private MainWindowViewModel CreateViewModel() =>
        new(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);

    [Fact]
    public void SavesTheRendererSettings()
    {
        var settings = new AppSettings();
        var viewModel = new SettingsViewModel(new ShortcutRegistry(), settings, _dialogs);

        // 既定は自動・GPU も自動・メモリの上限も自動
        Assert.Equal("auto", viewModel.Renderer.Name);
        Assert.Null(viewModel.GpuDevice.Name);
        Assert.Equal(0, viewModel.GpuMemoryLimit);
        Assert.True(viewModel.UsesGpu);
        Assert.Equal("（プロジェクトを開いていません）", viewModel.CurrentRenderer);
        Assert.False(viewModel.HasRendererProblem);

        viewModel.Renderer = viewModel.Renderers.Single(choice => choice.Name == "cpu");
        Assert.False(viewModel.UsesGpu); // CPU なら GPU の項目は変えられない
        viewModel.Renderer = viewModel.Renderers.Single(choice => choice.Name == "vulkan");
        viewModel.GpuMemoryLimit = 512;
        viewModel.ExperimentalVulkanVideo = true;
        viewModel.Apply(_environment.SettingsPath);

        var saved = AppSettings.Load(_environment.SettingsPath);
        Assert.Equal(("vulkan", (int?)512, true), (saved.Renderer, saved.GpuMemoryLimitMegabytes, saved.ExperimentalVulkanVideo));

        // 自動に戻すと、設定には書かない
        var again = new SettingsViewModel(new ShortcutRegistry(), saved, _dialogs);
        Assert.Equal("vulkan", again.Renderer.Name);
        Assert.Equal(512, again.GpuMemoryLimit);
        again.Renderer = again.Renderers[0];
        again.GpuMemoryLimit = 0;
        again.Apply(_environment.SettingsPath);
        saved = AppSettings.Load(_environment.SettingsPath);
        Assert.Equal((null, (int?)null), (saved.Renderer, saved.GpuMemoryLimitMegabytes));
    }

    [Fact]
    public void KeepsAGpuThatIsNotFoundNow()
    {
        // 設定にある GPU が今は見つからなくても、選んだまま（外したことにはしない）
        var settings = new AppSettings { RendererDevice = "見つからない GPU" };
        var viewModel = new SettingsViewModel(new ShortcutRegistry(), settings, _dialogs);
        Assert.Equal("見つからない GPU", viewModel.GpuDevice.Name);
        Assert.Contains("見つかりません", viewModel.GpuDevice.Label);

        viewModel.Apply(_environment.SettingsPath);
        Assert.Equal("見つからない GPU", AppSettings.Load(_environment.SettingsPath).RendererDevice);
    }

    [Fact]
    public Task UsesTheChosenRendererAndShowsIt() => Ui.Run(() =>
    {
        _environment.Settings.Renderer = "cpu";
        using var viewModel = CreateViewModel();

        Assert.Equal("Skia（CPU）", viewModel.Session.RendererName);
        Assert.Equal("描画：Skia（CPU）", viewModel.RendererLabel);
        Assert.False(viewModel.HasRendererProblem);

        // 設定の画面にも、今の描画を出す
        var settings = new SettingsViewModel(
            viewModel.Shortcuts, viewModel.Session.Settings, _dialogs, viewModel.Session.RendererName, viewModel.Session.RendererProblem);
        Assert.Equal("Skia（CPU）", settings.CurrentRenderer);
        Assert.Equal("cpu", settings.Renderer.Name);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TellsWhenTheGpuCannotBeUsed() => Ui.Run(() =>
    {
        // 前回 GPU の準備の途中で止まった（目印が残っている）ときは、GPU を選んでいても CPU で描き、そのことを知らせる
        _environment.Settings.Renderer = "vulkan";
        File.WriteAllText(Path.Combine(_environment.Folder, "gpu-starting"), "");
        using var viewModel = CreateViewModel();

        Assert.Equal("描画：Skia（CPU）", viewModel.RendererLabel);
        Assert.True(viewModel.HasRendererProblem);
        Assert.Contains("CPU で描きます", viewModel.RendererProblem);

        // 状態バーでは、警告の色で出し、理由をツールチップに出す
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        var label = window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text == "描画：Skia（CPU）");
        Assert.Contains("warning", label.Classes);
        Assert.Equal(viewModel.RendererProblem, ToolTip.GetTip(label));
        var settings = new SettingsViewModel(
            viewModel.Shortcuts, viewModel.Session.Settings, _dialogs, viewModel.Session.RendererName, viewModel.Session.RendererProblem);
        viewModel.Session.Document.MarkSaved();
        window.Close(); // 閉じると、プロジェクトも閉じる
        Assert.True(settings.HasRendererProblem);
        Assert.Contains("CPU で描きます", settings.RendererProblem);

        // 設定の「描画」のタブに、方式と理由が出る
        var dialog = new SettingsDialog { DataContext = settings };
        dialog.Show();
        var tabs = dialog.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => (string?)tab.Header == "描画");
        dialog.UpdateLayout();
        var texts = dialog.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text).ToList();
        Assert.Contains("Skia（CPU）", texts);
        Assert.Contains(settings.RendererProblem, texts);
        dialog.Close();
        return Task.CompletedTask;
    });
}
