using Avalonia.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>自動保存と復元、ショートカットの変更画面</summary>
public sealed class AutosaveAndSettingsTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private MainWindowViewModel CreateViewModel() =>
        new(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);

    private static void AddShape(EditorSession session, long start = 0) =>
        session.Execute(EditCommands.AddItem(
            session.Project,
            session.SceneId,
            new ShapeItem(ItemId.New(), session.Scene.Layers[0].Id, start, 30, new ShapeStyle(ShapeKind.Rectangle, 10, 10, new SolidPaint(RgbaColor.White)))));

    [Fact]
    public Task AutosavesOnlyUnsavedChangesAndClearsAfterSaving() => Ui.Run(async () =>
    {
        var entry = Path.Combine(_environment.Folder, "p", "p.nmmproj");
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        session.SaveAs(entry);

        Assert.False(viewModel.Autosave.SaveNow()); // 変更がなければ書かない
        AddShape(session);
        Assert.True(viewModel.Autosave.SaveNow());
        var recovery = Assert.Single(AutosaveService.Find(_environment.RecoveryFolder));
        Assert.Equal((entry, Path.GetDirectoryName(entry)!, "p"), (recovery.OriginalEntry, recovery.ProjectFolder, recovery.ProjectName));
        Assert.True(File.Exists(recovery.AutosaveEntry));

        // 保存すると、自動保存と記録を消す
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Empty(AutosaveService.Find(_environment.RecoveryFolder));
        Assert.False(File.Exists(recovery.AutosaveEntry));
    });

    [Fact]
    public Task RecoversAfterACrash() => Ui.Run(async () =>
    {
        var entry = Path.Combine(_environment.Folder, "p", "p.nmmproj");

        // 保存したあとに編集し、自動保存したところで（閉じずに）落ちたことにする
        var crashed = CreateViewModel();
        crashed.Session.SaveAs(entry);
        AddShape(crashed.Session);
        AddShape(crashed.Session, 60);
        crashed.Autosave.SaveNow();

        using var viewModel = CreateViewModel();
        _dialogs.Confirmations.Enqueue(true);
        await viewModel.RecoverAsync();

        var session = viewModel.Session;
        Assert.Equal(2, session.Scene.Items.Count);
        Assert.Equal(entry, session.EntryPath);
        Assert.Equal(Path.GetDirectoryName(entry), session.ProjectFolder);
        Assert.True(session.Document.IsModified);
        Assert.Contains("復元しました", viewModel.Status);

        // 保存すれば記録は消える。保存していないファイルは前のまま
        Assert.Single(AutosaveService.Find(_environment.RecoveryFolder));
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Empty(AutosaveService.Find(_environment.RecoveryFolder));
        Assert.Equal(2, new Core.Serialization.ProjectStore().Load(entry).Root.Items.Count);
    });

    [Fact]
    public Task RecoversUntitledProjectsOrDiscardsThem() => Ui.Run(async () =>
    {
        var crashed = CreateViewModel();
        var folder = crashed.Session.ProjectFolder;
        AddShape(crashed.Session);
        crashed.Autosave.SaveNow();

        // 復元しなければ、作業フォルダごと消す
        using (var declined = CreateViewModel())
        {
            _dialogs.Confirmations.Enqueue(false);
            await declined.RecoverAsync();
            Assert.Empty(declined.Session.Scene.Items);
        }

        Assert.False(Directory.Exists(folder));
        Assert.Empty(AutosaveService.Find(_environment.RecoveryFolder));

        var again = CreateViewModel();
        AddShape(again.Session);
        again.Autosave.SaveNow();
        using var viewModel = CreateViewModel();
        _dialogs.Confirmations.Enqueue(true);
        await viewModel.RecoverAsync();
        Assert.True(viewModel.Session.IsUntitled);
        Assert.Equal(again.Session.ProjectFolder, viewModel.Session.ProjectFolder);
        Assert.Single(viewModel.Session.Scene.Items);
    });

    [Fact]
    public void EditsShortcuts()
    {
        var registry = new ShortcutRegistry();
        var settings = new AppSettings();
        var viewModel = new SettingsViewModel(registry, settings);
        var split = viewModel.Shortcuts.Single(entry => entry.Command.Id == "edit.split");
        var save = viewModel.Shortcuts.Single(entry => entry.Command.Id == "file.save");

        // 選んで「キーを割り当てる」を押してから押したキーを割り当てる。修飾キーだけでは決まらない
        viewModel.Selected = split;
        viewModel.CaptureCommand.Execute(null);
        Assert.True(viewModel.OnKey(Key.LeftCtrl, KeyModifiers.Control));
        Assert.True(viewModel.IsCapturing);
        Assert.True(viewModel.OnKey(Key.S, KeyModifiers.Control));
        Assert.False(viewModel.IsCapturing);
        Assert.Equal(KeyGesture.Parse("Ctrl+S"), split.Gesture);
        Assert.Null(save.Gesture); // 保存から外れた
        Assert.Contains("保存", viewModel.Status);
        Assert.False(viewModel.OnKey(Key.A, KeyModifiers.None)); // 待っていなければ何もしない

        // Esc でやめる
        viewModel.CaptureCommand.Execute(null);
        viewModel.OnKey(Key.Escape, KeyModifiers.None);
        Assert.Equal(KeyGesture.Parse("Ctrl+S"), split.Gesture);

        viewModel.AutosaveMinutes = 10;
        var path = Path.Combine(Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"), "settings.json");
        viewModel.Apply(path);
        Assert.Equal(KeyGesture.Parse("Ctrl+S"), registry.GestureOf("edit.split"));
        Assert.Null(registry.GestureOf("file.save"));
        var saved = AppSettings.Load(path);
        Assert.Equal(("Ctrl+S", "", 10), (saved.Shortcuts["edit.split"], saved.Shortcuts["file.save"], saved.AutosaveMinutes));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);

        // 最初に戻す
        viewModel.ResetAllCommand.Execute(null);
        Assert.All(viewModel.Shortcuts, entry => Assert.False(entry.IsChanged));
    }
}
