using Avalonia.Threading;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>プロキシの画面（I-39）：プロジェクトの設定のプロキシ、メディアプールの状態と作る・消す、プレビューでの切り替え</summary>
public sealed class ProxyScreenTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public ProxyScreenTests() => _environment.Settings.Renderer = "cpu";

    public void Dispose() => _environment.Dispose();

    private MainWindowViewModel CreateViewModel() =>
        new(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);

    // UI のスレッドに送った知らせを、条件を満たすまで処理する
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "時間内に終わりませんでした");
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task ChangesTheProxyModeAndSavesIt() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var entry = Path.Combine(_environment.Folder, "p", "p.nmmproj");
        session.SaveAs(entry);
        _dialogs.ProjectSettings = settings =>
        {
            Assert.Equal(ProxyMode.LargeVideos, settings.ProxyMode.Mode); // 既定
            settings.ProxyMode = ProjectSettingsViewModel.ProxyModes.Single(choice => choice.Mode == ProxyMode.Off);
            return true;
        };

        await viewModel.ProjectSettingsCommand.ExecuteAsync(null);

        Assert.Equal(ProxyMode.Off, session.Project.Proxies.Mode);
        Assert.True(session.Save());
        Assert.Equal(ProxyMode.Off, new ProjectStore().Load(entry).Proxies.Mode);
        session.Document.Undo();
        Assert.Equal(ProxyMode.LargeVideos, session.Project.Proxies.Mode);
    });

    [Fact]
    public Task CreatesAndDeletesProxiesFromTheMediaPool() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var pool = viewModel.MediaPool;
        await pool.ImportAsync([Fixtures.Path("frames-2997.mp4"), Fixtures.Path("green-64x48.png")]);

        // 画像にはプロキシを作れない。小さい動画には、自動では作らない
        pool.Selected = pool.Assets.Single(asset => !asset.IsVideo);
        Assert.False(pool.CreateProxyCommand.CanExecute(null));
        var video = pool.Assets.Single(asset => asset.IsVideo);
        pool.Selected = video;
        Assert.False(video.HasProxyStatus);

        pool.CreateProxyCommand.Execute(null);
        Assert.True(video.HasProxyStatus); // 順番を待っている・作っている
        await WaitUntilAsync(() => video.ProxyStatus == "プロキシ：あり");
        Assert.True(session.Media.HasProxy(video.Id));
        Assert.True(session.Media.IsUsingProxy(video.Id)); // 作ったプロキシは、プレビューで使う

        // 一覧を作り直しても、状態はそのまま
        session.Execute(new Core.Editing.RenameProject("別の名前"));
        video = pool.Assets.Single(asset => asset.IsVideo);
        Assert.Equal("プロキシ：あり", video.ProxyStatus);

        pool.Selected = video;
        await pool.DeleteProxyCommand.ExecuteAsync(null);
        Assert.False(video.HasProxyStatus);
        Assert.False(session.Media.HasProxy(video.Id));
        Assert.Empty(_dialogs.Errors);
    });

    [Fact]
    public Task TurnsProxiesOffInThePreview() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        await viewModel.MediaPool.ImportAsync([Fixtures.Path("frames-2997.mp4")]);
        var video = viewModel.MediaPool.Assets.Single();
        viewModel.MediaPool.Selected = video;
        viewModel.MediaPool.CreateProxyCommand.Execute(null);
        await WaitUntilAsync(() => video.ProxyStatus == "プロキシ：あり");
        Assert.True(viewModel.Preview.UseProxies);
        Assert.True(session.Media.IsUsingProxy(video.Id));

        viewModel.Preview.UseProxies = false;

        Assert.False(session.Media.IsUsingProxy(video.Id));
        Assert.False(session.Settings.UseProxiesInPreview);

        // 次に開いたプロジェクトでも、使わないまま
        session.NewProject("次", session.Scene.Format);
        Assert.False(session.Media.UseProxies);
        Assert.False(viewModel.Preview.UseProxies);
    });
}
