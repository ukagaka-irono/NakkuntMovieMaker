using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.App.Views;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>
/// 画面での一連の作業（計画の確認項目）：新規作成 → 素材の追加 → 分割とリップル削除 → エフェクトの追加 → セリフの追加 → 保存。
/// メインウィンドウを開き、メニュー・パネルと同じコマンドで操作して、保存したプロジェクトを読み直して確かめる
/// </summary>
public sealed class EditingFlowTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public Task CreatesEditsAndSavesAProject() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        var window = new MainWindow();
        var viewModel = new MainWindowViewModel(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);
        window.DataContext = viewModel;
        window.Show();
        var session = viewModel.Session;

        // 1. 新規作成（縦長・30fps）
        _dialogs.ProjectSettings = settings =>
        {
            settings.Name = "解説動画";
            settings.Preset = ProjectSettingsViewModel.Presets.Single(preset => preset.Height == 1920);
            return true;
        };
        await viewModel.NewProjectCommand.ExecuteAsync(null);
        Assert.Equal(new SceneFormat(1080, 1920, 30, 48000), session.Scene.Format);

        // 2. 素材の追加と、タイムラインへの配置（画像は 5 秒）
        await viewModel.MediaPool.ImportAsync([Fixtures.Path("green-64x48.png"), Fixtures.Path("tone-1s.wav")]);
        viewModel.MediaPool.Selected = viewModel.MediaPool.Assets.Single(entry => entry.Name == "green-64x48.png");
        await viewModel.MediaPool.AddToTimelineCommand.ExecuteAsync(null);
        var image = session.SelectedItems.Single();
        Assert.Equal((0L, 150L), (image.Start, image.Length));

        // 3. 分割して、前半を詰めて削除する
        session.CurrentFrame = 60;
        await viewModel.Timeline.SplitCommand.ExecuteAsync(null);
        session.Selection = [image.Id];
        await viewModel.Timeline.RippleDeleteCommand.ExecuteAsync(null);
        var remaining = Assert.Single(session.Scene.Items.Values.OfType<ImageItem>());
        Assert.Equal((0L, 90L), (remaining.Start, remaining.Length));

        // 4. エフェクトの追加（プロパティパネル）
        session.Selection = [remaining.Id];
        viewModel.Properties.AddEffectCommand.Execute(PropertiesViewModel.EffectDefinitions.Single(definition => definition.Type == "nmm.blur"));
        Assert.Equal("nmm.blur", Assert.Single(session.Scene.GetItem(remaining.Id).Effects).Type);

        // 5. キャラクターを作り、セリフ入力欄でセリフを置く
        viewModel.Characters.AddCommand.Execute(null);
        viewModel.Characters.Name = "霊夢";
        viewModel.Characters.CommitName();
        session.CurrentFrame = 15;
        viewModel.Dialogue.Text = "ゆっくりしていってね";
        await viewModel.Dialogue.AddCommand.ExecuteAsync(null);
        var line = Assert.IsType<VoiceItem>(session.SelectedItems.Single());
        Assert.Equal((15L, "霊夢"), (line.Start, session.Project.Characters[line.Character].Name));

        // 6. 名前を付けて保存し、読み直して確かめる
        var entry = Path.Combine(_environment.Folder, "解説動画", "解説動画.nmmproj");
        _dialogs.FilesToSave.Enqueue(entry);
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_dialogs.Errors);
        Assert.Equal("解説動画 - NakkuntMovieMaker", viewModel.Title);

        var saved = new ProjectStore().Load(entry);
        Assert.Equal(session.Project, saved);
        var savedLine = saved.Root.Items.Values.OfType<VoiceItem>().Single();
        var key = Assert.IsType<SynthesizedVoice>(savedLine.Source).CacheKey;
        Assert.True(File.Exists(Path.Combine(_environment.Folder, "解説動画", "voices", key + ".wav")), "声も保存先に移っている");

        window.Close();
    });
}
