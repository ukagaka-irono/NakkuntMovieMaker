using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Serialization;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>メインウィンドウ。ファイルの操作・元に戻す・プロジェクトの設定と、各パネルをまとめる</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;

    private readonly string _recoveryFolder;
    private readonly string _settingsPath;

    /// <param name="openInput">録音の入力デバイスを開く（テストでは差し替える）</param>
    /// <param name="recoveryFolder">自動保存の復元の記録を置くフォルダ（テストでは差し替える）</param>
    /// <param name="settingsPath">設定ファイル（テストでは差し替える）</param>
    public MainWindowViewModel(
        EditorSession session,
        ShortcutRegistry shortcuts,
        IDialogService dialogs,
        Func<string?, Audio.IAudioInput>? openInput = null,
        string? recoveryFolder = null,
        string? settingsPath = null)
    {
        _recoveryFolder = recoveryFolder ?? AutosaveService.DefaultRecoveryFolder;
        _settingsPath = settingsPath ?? AppSettings.DefaultPath;
        Autosave = new AutosaveService(session, _recoveryFolder);
        Session = session;
        Shortcuts = shortcuts;
        _dialogs = dialogs;
        MediaPool = new MediaPoolViewModel(session, dialogs);
        Preview = new PreviewViewModel(session);
        Timeline = new TimelineViewModel(session, dialogs);
        var fonts = PropertiesViewModel.SystemFonts();
        TextPresets = new TextPresets(session.Settings);
        TextPresets.Changed += (_, _) => RefreshPresets();
        RefreshPresets();
        Properties = new PropertiesViewModel(session, dialogs, fonts, TextPresets);
        VoicePlayer = new VoicePlayer(session.DisableAudioDevice, () => session.Settings.OutputDevice);
        Dialogue = new DialogueViewModel(session, dialogs, VoicePlayer);
        Recording = new RecordingViewModel(session, dialogs, Preview, openInput);
        Characters = new CharactersViewModel(session, dialogs, VoicePlayer, fonts);
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
            RefreshRenderer();
        };
        // 描いている途中で GPU が使えなくなって CPU に切り替えた（描画のスレッドから呼ばれる）
        session.RendererChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshRenderer();
            Status = $"GPU で描けなくなったので、CPU で描いています（{Session.RendererProblem}）";
        });
        RefreshRecent();

        shortcuts.Bind("file.new", NewProjectCommand);
        shortcuts.Bind("file.open", OpenProjectCommand);
        shortcuts.Bind("file.save", SaveCommand);
        shortcuts.Bind("file.saveAs", SaveAsCommand);
        shortcuts.Bind("file.projectSettings", ProjectSettingsCommand);
        shortcuts.Bind("file.export", ExportCommand);
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
        shortcuts.Bind("edit.cut", Timeline.CutCommand);
        shortcuts.Bind("edit.copy", Timeline.CopyCommand);
        shortcuts.Bind("edit.paste", Timeline.PasteCommand);
        shortcuts.Bind("edit.delete", Timeline.DeleteCommand);
        shortcuts.Bind("edit.rippleDelete", Timeline.RippleDeleteCommand);
        shortcuts.Bind("edit.split", Timeline.SplitCommand);
        shortcuts.Bind("edit.duplicate", Timeline.DuplicateCommand);
        shortcuts.Bind("edit.group", Timeline.GroupCommand);
        shortcuts.Bind("edit.ungroup", Timeline.UngroupCommand);
        shortcuts.Bind("edit.selectAll", Timeline.SelectAllCommand);
        shortcuts.Bind("timeline.marker", Timeline.AddMarkerCommand);
        shortcuts.Bind("timeline.rangeIn", Timeline.SetRangeStartCommand);
        shortcuts.Bind("timeline.rangeOut", Timeline.SetRangeEndCommand);
        shortcuts.Bind("timeline.clearRange", Timeline.ClearRangeCommand);
        shortcuts.Bind("timeline.zoomIn", Timeline.ZoomInCommand);
        shortcuts.Bind("timeline.zoomOut", Timeline.ZoomOutCommand);
        shortcuts.Bind("timeline.snap", Timeline.ToggleSnapCommand);
        shortcuts.Bind("dialogue.focus", Dialogue.FocusCommand);
        shortcuts.Bind("record.toggle", Recording.ToggleRecordCommand);
        shortcuts.Bind("item.addText", Timeline.AddTextCommand);
    }

    public EditorSession Session { get; }

    public ShortcutRegistry Shortcuts { get; }

    public IDialogService Dialogs => _dialogs;

    public MediaPoolViewModel MediaPool { get; }

    public PreviewViewModel Preview { get; }

    public TimelineViewModel Timeline { get; }

    public PropertiesViewModel Properties { get; }

    public VoicePlayer VoicePlayer { get; }

    public DialogueViewModel Dialogue { get; }

    public RecordingViewModel Recording { get; }

    public CharactersViewModel Characters { get; }

    public AutosaveService Autosave { get; }

    public TextPresets TextPresets { get; }

    /// <summary>メニューの「追加 → テキスト」に並べるプリセット</summary>
    public ObservableCollection<TextPreset> TextPresetList { get; } = [];

    public string Title => Session.Title;

    /// <summary>メニューの「最近使ったプロジェクト」</summary>
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>状態の表示（画面の下）</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "準備完了";

    /// <summary>状態バーに出す、今の描画の方式</summary>
    public string RendererLabel => Session.RendererName is { } name ? $"描画：{name}" : "";

    /// <summary>望んだ描画の方式を使えずに CPU に切り替えた理由（なければ null）</summary>
    public string? RendererProblem => Session.RendererProblem;

    public bool HasRendererProblem => RendererProblem is not null;

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

    /// <summary>
    /// 前回、保存していない変更を残したまま終わっていたら（自動保存が残っていれば）、復元するか聞く。
    /// 復元しなければ自動保存を消す。そのあと自動保存を始める
    /// </summary>
    public async Task RecoverAsync()
    {
        foreach (var entry in AutosaveService.Find(_recoveryFolder))
        {
            var place = entry.OriginalEntry ?? "保存していないプロジェクト";
            if (await _dialogs.ConfirmAsync(
                    "自動保存からの復元",
                    $"前回、「{entry.ProjectName}」（{place}）を保存せずに終了しました。{entry.SavedAt.LocalDateTime:M月d日 H:mm} の自動保存から復元しますか？\n復元しない場合、自動保存は消えます。",
                    "復元する"))
            {
                try
                {
                    Session.OpenRecovered(entry.AutosaveEntry, entry.ProjectFolder, entry.OriginalEntry);
                    Autosave.Adopt(entry);
                    Status = "自動保存から復元しました。保存してください";
                    break;
                }
                catch (Exception ex) when (ex is Core.Serialization.ProjectFormatException or IOException or UnauthorizedAccessException)
                {
                    await _dialogs.ShowErrorAsync("復元できません", ex.Message);
                }
            }

            AutosaveService.Discard(entry);
        }

        Autosave.Start();
    }

    /// <summary>設定（ショートカット・自動保存・出力デバイス・外部の ffmpeg・描画）</summary>
    [RelayCommand]
    private async Task SettingsAsync()
    {
        var settings = new SettingsViewModel(Shortcuts, Session.Settings, _dialogs, Session.RendererName, Session.RendererProblem);
        if (await _dialogs.ShowDialogAsync<bool?>(new Views.Dialogs.SettingsDialog(), settings) == true)
        {
            settings.Apply(_settingsPath);
            Autosave.Start();
        }
    }

    /// <summary>書き出しダイアログを開く</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Preview.IsPlaying)
        {
            Preview.StopCommand.Execute(null);
        }

        await _dialogs.ShowDialogAsync<object>(new Views.Dialogs.ExportDialog(), new ExportViewModel(Session, _dialogs));
    }

    /// <summary>
    /// 字幕ファイル（SRT）を読み込み、新しいレイヤーに並べる。セリフ入力欄でキャラクターを選んでいればセリフに（声はあとで作る）、
    /// なければ字幕のプリセットのテキストにする。字幕の 0 秒は再生位置に置く
    /// </summary>
    [RelayCommand]
    private async Task ImportSubtitlesAsync()
    {
        if (await _dialogs.PickOpenFileAsync("字幕の読み込み", [FileKinds.Subtitles, FileKinds.All]) is not { } path)
        {
            return;
        }

        IReadOnlyList<Engine.Subtitles.SubtitleCue> cues;
        try
        {
            cues = Engine.Subtitles.Srt.ReadFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            await _dialogs.ShowErrorAsync("字幕を読み込めません", ex.Message);
            return;
        }

        if (cues.Count == 0)
        {
            await _dialogs.ShowErrorAsync("字幕を読み込めません", "字幕が 1 つも見つかりませんでした");
            return;
        }

        var project = Session.Project;
        var sceneId = Session.SceneId;
        var addLayer = EditCommands.AddLayer(project, sceneId, Session.Scene.Layers.Count, $"字幕（{Path.GetFileNameWithoutExtension(path)}）");
        project = addLayer.Apply(project).Project;
        var layer = project.GetScene(sceneId).Layers[^1].Id;
        var character = Dialogue.Character?.Id;
        try
        {
            var (import, items) = Engine.Subtitles.SubtitleFiles.Import(project, sceneId, layer, cues, Session.CurrentFrame, character, TextPresets.BuiltIn[0].Style);
            if (Session.Execute(Operations.Combine("字幕の読み込み", [addLayer, import])) is { } error)
            {
                await _dialogs.ShowErrorAsync("字幕を読み込めません", error);
                return;
            }

            Session.Selection = [.. items];
            Status = character is null
                ? $"{items.Count} 個の字幕をテキストとして読み込みました"
                : $"{items.Count} 個の字幕をセリフとして読み込みました（声は「声を作り直す」で作れます）";
        }
        catch (EditException ex)
        {
            await _dialogs.ShowErrorAsync("字幕を読み込めません", ex.Message);
        }
    }

    /// <summary>セリフとテキストを字幕ファイル（SRT）に書き出す（範囲を指定していれば、その範囲）</summary>
    [RelayCommand]
    private async Task ExportSubtitlesAsync()
    {
        if (await _dialogs.PickSaveFileAsync("字幕の書き出し", Session.Project.Name + ".srt", [FileKinds.Subtitles]) is not { } path)
        {
            return;
        }

        var cues = Engine.Subtitles.SubtitleFiles.Export(Session.Scene, includeText: true, from: Session.RangeStart, to: Session.RangeEnd);
        try
        {
            Engine.Subtitles.Srt.WriteFile(path, cues);
            Status = $"{cues.Count} 個の字幕を {path} に書き出しました";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("字幕を書き出せません", ex.Message);
        }
    }

    /// <summary>再生位置のフレームを画像に書き出す</summary>
    [RelayCommand]
    private async Task ExportStillAsync()
    {
        var frame = Session.CurrentFrame;
        if (await _dialogs.PickSaveFileAsync("静止画の書き出し", $"{Session.Project.Name}_{frame}.png", [FileKinds.Image]) is not { } path)
        {
            return;
        }

        try
        {
            using var pipeline = new Engine.Export.ExportPipeline(Session.Project, Session.SceneId, Session.ProjectFolder);
            await pipeline.ExportStillAsync(path, frame);
            Status = $"{path} に書き出しました";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("書き出せません", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session.Document.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session.Document.Redo();

    public void Dispose()
    {
        Autosave.Dispose();
        Recording.Dispose();
        VoicePlayer.Dispose();
        Session.Dispose();
    }

    private void RefreshRenderer()
    {
        OnPropertyChanged(nameof(RendererLabel));
        OnPropertyChanged(nameof(RendererProblem));
        OnPropertyChanged(nameof(HasRendererProblem));
    }

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

    private void RefreshPresets()
    {
        TextPresetList.Clear();
        foreach (var preset in TextPresets.All)
        {
            TextPresetList.Add(preset);
        }
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
