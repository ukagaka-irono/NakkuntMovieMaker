using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.ColorManagement;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>
/// 色の画面（I-38）：プロジェクトの設定の色（合成の色空間・OpenColorIO の構成・書き出しの色空間）、
/// 設定のプレビューの表示の色空間、メディアプールでの素材ごとの入力の色空間。変えると、描くフレームの色の設定と保存に反映される
/// </summary>
public sealed class ColorSettingsTests : IDisposable
{
    private const string DisplayP3 = "Display P3 - Display";

    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public ColorSettingsTests() => _environment.Settings.Renderer = "cpu";

    public void Dispose() => _environment.Dispose();

    private MainWindowViewModel CreateViewModel() =>
        new(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);

    private static void RequireOcio()
    {
        var loaded = Ocio.TryLoad(out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_OCIO") != "1", $"nmm_ocio を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    private static Task<Engine.Playback.RenderedFrame> RenderAsync(EditorSession session) =>
        session.Frames.RequestAsync(0, TestContext.Current.CancellationToken);

    [Fact]
    public Task ChangesTheCompositingSpaceAndSavesIt() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var entry = Path.Combine(_environment.Folder, "p", "p.nmmproj");
        session.SaveAs(entry);
        Assert.Equal(CompositingSpace.Srgb, (await RenderAsync(session)).Plan.Color.Compositing);

        _dialogs.ProjectSettings = settings =>
        {
            Assert.Equal(CompositingSpace.Srgb, settings.Compositing.Space); // 今の設定が選ばれている
            Assert.Null(settings.OcioConfig.Name);
            Assert.Null(settings.ExportColorSpace.Name);
            settings.Compositing = ProjectSettingsViewModel.CompositingChoices.Single(choice => choice.Space == CompositingSpace.Linear);
            return true;
        };
        await viewModel.ProjectSettingsCommand.ExecuteAsync(null);

        Assert.Equal(CompositingSpace.Linear, session.Project.Color.Compositing);
        Assert.Equal(CompositingSpace.Linear, (await RenderAsync(session)).Plan.Color.Compositing); // プレビューもリニアで描く
        Assert.Equal("元に戻す（プロジェクトの設定）", viewModel.UndoHeader);

        Assert.True(session.Save());
        Assert.Equal(CompositingSpace.Linear, new ProjectStore().Load(entry).Color.Compositing);

        // 元に戻すと、sRGB で描く
        session.Document.Undo();
        Assert.Equal(CompositingSpace.Srgb, (await RenderAsync(session)).Plan.Color.Compositing);
    });

    [Fact]
    public Task ChoosesTheConfigAndExportColorSpace() => Ui.Run(async () =>
    {
        RequireOcio();
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var config = Ocio.BuiltinConfigs.First(config => config.Name != Ocio.DefaultBuiltinConfig).Name;
        _dialogs.ProjectSettings = settings =>
        {
            Assert.StartsWith("既定（", settings.OcioConfigs[0].Label);
            Assert.False(settings.IsOcioUnavailable);
            settings.ExportColorSpace = settings.ExportColorSpaces.Single(choice => choice.Name == DisplayP3);
            settings.OcioConfig = settings.OcioConfigs.Single(choice => choice.Name == config);
            Assert.Equal(DisplayP3, settings.ExportColorSpace.Name); // 構成を変えても、同じ名前があれば選んだまま
            return true;
        };
        await viewModel.ProjectSettingsCommand.ExecuteAsync(null);

        Assert.Equal(new ProjectColorSettings(CompositingSpace.Srgb, config, DisplayP3), session.Project.Color);

        // 開き直すと、今の設定が選ばれている
        var reopened = new ProjectSettingsViewModel("プロジェクトの設定", "p", session.Scene.Format, isNew: false, session.Project.Color);
        Assert.Equal(config, reopened.OcioConfig.Name);
        Assert.Equal(DisplayP3, reopened.ExportColorSpace.Name);
    });

    [Fact]
    public void KeepsAColorSpaceThatIsNotInTheConfig()
    {
        // ほかの構成で選んだ色空間などは、勝手に変えない
        var settings = new ProjectSettingsViewModel("プロジェクトの設定", "p", new SceneFormat(1920, 1080, 30, 48000), isNew: false,
            new ProjectColorSettings(ExportColorSpace: "存在しない色空間"));
        Assert.Equal("存在しない色空間", settings.ExportColorSpace.Name);
        Assert.Contains("見つかりません", settings.ExportColorSpace.Label);
        Assert.Equal("存在しない色空間", settings.Color.ExportColorSpace);
    }

    [Fact]
    public Task NewProjectsUseTheChosenColor() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        _dialogs.ProjectSettings = settings =>
        {
            settings.Compositing = ProjectSettingsViewModel.CompositingChoices.Single(choice => choice.Space == CompositingSpace.Linear);
            return true;
        };

        await viewModel.NewProjectCommand.ExecuteAsync(null);

        Assert.Equal(CompositingSpace.Linear, viewModel.Session.Project.Color.Compositing);
        Assert.False(viewModel.Session.Document.CanUndo); // 新しいプロジェクトの最初の状態
    });

    [Fact]
    public Task AppliesThePreviewColorSpaceRightAway() => Ui.Run(async () =>
    {
        RequireOcio();
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        Assert.Null((await RenderAsync(session)).Plan.Color.Output);

        _dialogs.Dialog = model =>
        {
            var settings = Assert.IsType<SettingsViewModel>(model);
            Assert.Null(settings.PreviewColorSpace.Name);
            settings.PreviewColorSpace = settings.PreviewColorSpaces.Single(choice => choice.Name == DisplayP3);
            return true;
        };
        await viewModel.SettingsCommand.ExecuteAsync(null);

        Assert.Equal(DisplayP3, session.Settings.PreviewColorSpace);
        Assert.Equal(DisplayP3, AppSettings.Load(_environment.SettingsPath).PreviewColorSpace);
        Assert.NotNull((await RenderAsync(session)).Plan.Color.Output); // 開き直さなくても、すぐに表示の変換をかける
    });

    [Fact]
    public Task SetsTheInputColorSpaceOfAnAsset() => Ui.Run(async () =>
    {
        RequireOcio();
        Fixtures.RequireFfmpeg();
        using var session = _environment.CreateSession();
        var pool = new MediaPoolViewModel(session, _dialogs);
        await pool.ImportAsync([Fixtures.Path("green-64x48.png"), Fixtures.Path("tone-1s.wav")]);
        await pool.ColorErrorsReady;

        Assert.Null(pool.InputColorSpaces[0].Name); // 先頭は自動
        Assert.Contains(pool.InputColorSpaces, choice => choice.Name == DisplayP3);

        // 音声には指定できない
        pool.Selected = pool.Assets.Single(entry => entry.IsAudio);
        Assert.False(pool.SetInputColorSpaceCommand.CanExecute(pool.InputColorSpaces[1]));

        pool.Selected = pool.Assets.Single(entry => !entry.IsAudio);
        Assert.False(pool.Selected.HasColorSpace);
        pool.SetInputColorSpaceCommand.Execute(pool.InputColorSpaces.Single(choice => choice.Name == DisplayP3));
        await pool.ColorErrorsReady;

        var image = pool.Assets.Single(entry => !entry.IsAudio);
        Assert.Equal(DisplayP3, session.Project.Assets[image.Id].ColorSpace);
        Assert.Equal($"入力の色空間：{DisplayP3}", image.ColorSpaceLabel);
        Assert.False(image.HasColorError);

        // 変換できない色空間は、理由を出す（変換せずに描く）
        session.Execute(new SetAsset(image.Id, image.Asset with { ColorSpace = "存在しない色空間" }));
        await pool.ColorErrorsReady;
        image = pool.Assets.Single(entry => !entry.IsAudio);
        Assert.True(image.HasColorError);

        // 自動に戻す
        pool.Selected = image;
        pool.SetInputColorSpaceCommand.Execute(pool.InputColorSpaces[0]);
        Assert.Null(session.Project.Assets[image.Id].ColorSpace);
        Assert.False(pool.Assets.Single(entry => !entry.IsAudio).HasColorSpace);
    });
}
