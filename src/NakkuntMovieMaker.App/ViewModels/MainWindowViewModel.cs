using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Serialization;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>メインウィンドウ。ファイルの操作・元に戻す・プロジェクトの設定と、各パネルをまとめる</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;

    public MainWindowViewModel(EditorSession session, ShortcutRegistry shortcuts, IDialogService dialogs)
    {
        Session = session;
        Shortcuts = shortcuts;
        _dialogs = dialogs;
        MediaPool = new MediaPoolViewModel(session, dialogs);
        Preview = new PreviewViewModel(session);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorSession.Title))
            {
                OnPropertyChanged(nameof(Title));
            }
        };
        session.Changed += (_, _) => RefreshEditState();
        session.ProjectOpened += (_, _) =>
        {
            RefreshEditState();
            RefreshRecent();
        };
        RefreshRecent();

        shortcuts.Bind("file.new", NewProjectCommand);
        shortcuts.Bind("file.open", OpenProjectCommand);
        shortcuts.Bind("file.save", SaveCommand);
        shortcuts.Bind("file.saveAs", SaveAsCommand);
        shortcuts.Bind("file.projectSettings", ProjectSettingsCommand);
        shortcuts.Bind("edit.undo", UndoCommand);
        shortcuts.Bind("edit.redo", RedoCommand);
        shortcuts.Bind("play.toggle", Preview.TogglePlayCommand);
        shortcuts.Bind("play.backward", Preview.BackwardCommand);
        shortcuts.Bind("play.pause", Preview.StopCommand);
        shortcuts.Bind("play.forward", Preview.ForwardCommand);
        shortcuts.Bind("play.previousFrame", Preview.PreviousFrameCommand);
        shortcuts.Bind("play.nextFrame", Preview.NextFrameCommand);
        shortcuts.Bind("play.start", Preview.GoToStartCommand);
        shortcuts.Bind("play.end", Preview.GoToEndCommand);
        shortcuts.Bind("view.fullScreenPreview", Preview.ToggleFullScreenCommand);
    }

    public EditorSession Session { get; }

    public ShortcutRegistry Shortcuts { get; }

    public IDialogService Dialogs => _dialogs;

    public MediaPoolViewModel MediaPool { get; }

    public PreviewViewModel Preview { get; }

    public string Title => Session.Title;

    /// <summary>メニューの「最近使ったプロジェクト」</summary>
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>状態の表示（画面の下）</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "準備完了";

    public string UndoHeader => Session.Document.UndoDescription is { } description ? $"元に戻す（{description}）" : "元に戻す";

    public string RedoHeader => Session.Document.RedoDescription is { } description ? $"やり直し（{description}）" : "やり直し";

    /// <summary>ウィンドウを閉じる前の確認。閉じてよければ true</summary>
    public async Task<bool> ConfirmCloseAsync() => await ConfirmDiscardAsync();

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        var settings = new ProjectSettingsViewModel("新規作成", "新しいプロジェクト", Session.Scene.Format, isNew: true);
        if (await _dialogs.ShowProjectSettingsAsync(settings))
        {
            Session.NewProject(settings.Name, settings.Format);
            Status = "新しいプロジェクトを作りました";
        }
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (await ConfirmDiscardAsync() && await _dialogs.PickOpenFileAsync("プロジェクトを開く", [FileKinds.Project]) is { } path)
        {
            await OpenAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string path)
    {
        if (await ConfirmDiscardAsync())
        {
            await OpenAsync(path);
        }
    }

    [RelayCommand]
    private async Task<bool> SaveAsync()
    {
        if (Session.IsUntitled)
        {
            return await SaveAsAsync();
        }

        try
        {
            Session.Save();
            Status = "保存しました";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("保存できません", ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private async Task<bool> SaveAsAsync()
    {
        // プロジェクトはフォルダ形式なので、保存先にはプロジェクトの名前のフォルダを作ることを勧める
        var path = await _dialogs.PickSaveFileAsync("名前を付けて保存", Session.Project.Name + ProjectStore.EntryExtension, [FileKinds.Project]);
        if (path is null)
        {
            return false;
        }

        if (!path.EndsWith(ProjectStore.EntryExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += ProjectStore.EntryExtension;
        }

        try
        {
            Session.SaveAs(path);
            Status = $"{path} に保存しました";
            RefreshRecent();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("保存できません", ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private async Task ProjectSettingsAsync()
    {
        var scene = Session.Scene;
        var settings = new ProjectSettingsViewModel("プロジェクトの設定", Session.Project.Name, scene.Format, isNew: false);
        if (!await _dialogs.ShowProjectSettingsAsync(settings))
        {
            return;
        }

        var operations = new List<IOperation>();
        if (settings.Name != Session.Project.Name)
        {
            operations.Add(new RenameProject(settings.Name));
        }

        if (settings.Format != scene.Format)
        {
            operations.Add(EditCommands.ChangeSceneSettings(Session.Project, Session.SceneId, scene.Name, settings.Format));
        }

        if (operations.Count > 0 && Session.Execute(Operations.Combine("プロジェクトの設定", operations)) is { } error)
        {
            await _dialogs.ShowErrorAsync("設定を変えられません", error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session.Document.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session.Document.Redo();

    public void Dispose() => Session.Dispose();

    private bool CanUndo() => Session.Document.CanUndo;

    private bool CanRedo() => Session.Document.CanRedo;

    private async Task OpenAsync(string path)
    {
        try
        {
            Session.Open(path);
            Status = $"{Path.GetFileName(path)} を開きました";
            var missing = Core.Assets.AssetLinks.FindMissing(Session.Project, Session.ProjectFolder);
            if (missing.Count > 0)
            {
                Status = $"見つからない素材が {missing.Count} 個あります（素材の一覧で場所を指定できます）";
            }
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("プロジェクトを開けません", ex.Message);
            Session.Settings.RecentProjects.Remove(path);
        }

        RefreshRecent();
    }

    // 保存していない変更があれば、保存するか聞く。続けてよければ true
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!Session.Document.IsModified)
        {
            return true;
        }

        return await _dialogs.AskUnsavedChangesAsync(Session.Project.Name) switch
        {
            UnsavedChangesChoice.Save => await SaveAsync(),
            UnsavedChangesChoice.Discard => true,
            _ => false,
        };
    }

    private void RefreshEditState()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoHeader));
        OnPropertyChanged(nameof(RedoHeader));
        OnPropertyChanged(nameof(Title));
    }

    private void RefreshRecent()
    {
        RecentProjects.Clear();
        foreach (var path in Session.Settings.RecentProjects)
        {
            RecentProjects.Add(path);
        }
    }
}
