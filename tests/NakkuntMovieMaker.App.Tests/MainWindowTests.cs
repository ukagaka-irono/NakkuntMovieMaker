using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.App.Views;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>メインウィンドウ：新規作成・保存・開く・プロジェクトの設定・元に戻す・ショートカット</summary>
public sealed class MainWindowTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private MainWindowViewModel CreateViewModel() => new(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);

    private static IOperation AddShape(EditorSession session, long start = 0) =>
        EditCommands.AddItem(
            session.Project,
            session.SceneId,
            new ShapeItem(ItemId.New(), session.Scene.Layers[0].Id, start, 30, new ShapeStyle(ShapeKind.Rectangle, 100, 100, new SolidPaint(RgbaColor.White))));

    [Fact]
    public Task SavesUntitledProjectsWithANameAndMovesTheirFiles() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var untitled = session.ProjectFolder;
        Assert.True(session.IsUntitled);
        Assert.Contains("（未保存）", viewModel.Title);

        // 一時フォルダの録音は、保存先へ移す
        Directory.CreateDirectory(Path.Combine(untitled, "recordings"));
        await File.WriteAllTextAsync(Path.Combine(untitled, "recordings", "a.wav"), "x", TestContext.Current.CancellationToken);
        session.Execute(AddShape(session));
        Assert.StartsWith("● ", viewModel.Title);

        var entry = Path.Combine(_environment.Folder, "動画", "解説.nmmproj");
        _dialogs.FilesToSave.Enqueue(Path.Combine(_environment.Folder, "動画", "解説"));
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(entry));
        Assert.True(File.Exists(Path.Combine(_environment.Folder, "動画", "recordings", "a.wav")));
        Assert.False(Directory.Exists(untitled));
        Assert.Equal(("解説", entry), (session.Project.Name, session.EntryPath));
        Assert.False(session.Document.IsModified);
        Assert.Equal("解説 - NakkuntMovieMaker", viewModel.Title);
        Assert.Equal([entry], viewModel.RecentProjects);

        // 保存してあれば、そのまま上書きする
        session.Execute(AddShape(session, 60));
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(session.Document.IsModified);
        Assert.Empty(_dialogs.FilesToSave);
    });

    [Fact]
    public Task OpensProjectsAndAsksBeforeDiscardingChanges() => Ui.Run(async () =>
    {
        var entry = Path.Combine(_environment.Folder, "a", "a.nmmproj");
        using (var first = CreateViewModel())
        {
            first.Session.Execute(AddShape(first.Session));
            first.Session.SaveAs(entry);
        }

        using var viewModel = CreateViewModel();
        viewModel.Session.Execute(AddShape(viewModel.Session));

        // キャンセルすると開かない
        _dialogs.UnsavedAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        _dialogs.FilesToOpen.Enqueue(entry);
        await viewModel.OpenProjectCommand.ExecuteAsync(null);
        Assert.True(viewModel.Session.IsUntitled);

        _dialogs.UnsavedAnswers.Enqueue(UnsavedChangesChoice.Discard);
        await viewModel.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(entry, viewModel.Session.EntryPath);
        Assert.Single(viewModel.Session.Scene.Items);
        Assert.False(viewModel.UndoCommand.CanExecute(null));

        // 開けないファイルは知らせる
        var broken = Path.Combine(_environment.Folder, "broken.nmmproj");
        await File.WriteAllTextAsync(broken, "{", TestContext.Current.CancellationToken);
        await viewModel.OpenRecentCommand.ExecuteAsync(broken);
        Assert.Single(_dialogs.Errors);
        Assert.Equal(entry, viewModel.Session.EntryPath);
    });

    [Fact]
    public Task CreatesNewProjectsWithTheChosenFormat() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        _dialogs.ProjectSettings = settings =>
        {
            settings.Name = "縦長";
            settings.Preset = ProjectSettingsViewModel.Presets.Single(preset => preset.Height == 1920);
            settings.FrameRate = settings.FrameRates.Single(choice => choice.Fps == 60);
            return true;
        };

        await viewModel.NewProjectCommand.ExecuteAsync(null);

        Assert.Equal(("縦長", new SceneFormat(1080, 1920, 60, 48000)), (viewModel.Session.Project.Name, viewModel.Session.Scene.Format));
    });

    [Fact]
    public Task ChangesProjectSettingsUndoably() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        session.Execute(AddShape(session, 30));
        _dialogs.ProjectSettings = settings =>
        {
            settings.Name = "変更後";
            settings.FrameRate = settings.FrameRates.Single(choice => choice.Fps == Rational.Fps59_94);
            return true;
        };

        await viewModel.ProjectSettingsCommand.ExecuteAsync(null);

        Assert.Equal("変更後", session.Project.Name);
        Assert.Equal(Rational.Fps59_94, session.Scene.Fps);
        Assert.Equal(60, session.Scene.Items.Values.Single().Start); // 1 秒の位置のまま
        Assert.Equal("元に戻す（プロジェクトの設定）", viewModel.UndoHeader);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal((30L, (Rational)30), (session.Scene.Items.Values.Single().Start, session.Scene.Fps));
        Assert.True(viewModel.RedoCommand.CanExecute(null));
    });

    [Fact]
    public void ValidatesProjectSettings()
    {
        var settings = new ProjectSettingsViewModel("設定", "名前", SceneFormat.Landscape1080p30, isNew: true);
        Assert.True(settings.IsValid);

        settings.Width = 1919;
        Assert.Contains("偶数", settings.Error);
        settings.Width = 1920;
        settings.Name = "a/b";
        Assert.Contains("使えない文字", settings.Error);
        settings.Name = " ";
        Assert.False(settings.IsValid);
    }

    [Fact]
    public Task ShowsShortcutsInMenusAndHandlesKeys() => Ui.Run(() =>
    {
        var window = new MainWindow();
        var viewModel = CreateViewModel();
        window.DataContext = viewModel;
        window.Show();

        var save = window.GetLogicalDescendants().OfType<MenuItem>().Single(item => (string?)item.Tag == "file.save");
        Assert.Equal(KeyGesture.Parse("Ctrl+S"), save.InputGesture);

        viewModel.Session.Execute(AddShape(viewModel.Session));
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Empty(viewModel.Session.Scene.Items);

        // 割り当てを変えると、メニューの表示も変わる
        viewModel.Shortcuts.Assign("file.save", KeyGesture.Parse("F2"));
        Assert.Equal(KeyGesture.Parse("F2"), save.InputGesture);

        // 保存していない変更がなければ、確認せずに閉じる
        viewModel.Session.Document.MarkSaved();
        window.Close();
        Assert.Empty(_dialogs.UnsavedAnswers);
    });
}
