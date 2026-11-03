using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>
/// セリフ入力欄。キャラクターを選んで文を入れ、Enter で再生位置にセリフを置いて声を作る（長さは声に合わせる）。
/// 続けて入れられるよう、再生位置を置いたセリフの終わりに進める。読みの修正、試聴、選んだセリフの声の作り直し、台本の一括貼り付け
/// </summary>
public sealed partial class DialogueViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private readonly VoicePlayer _player;
    private LayerId? _lastLayer;

    public DialogueViewModel(EditorSession session, IDialogService dialogs, VoicePlayer player)
    {
        _session = session;
        _dialogs = dialogs;
        _player = player;
        session.Changed += (_, _) => RefreshCharacters();
        session.ProjectOpened += (_, _) =>
        {
            _lastLayer = null;
            RefreshCharacters();
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorSession.Selection))
            {
                ResynthesizeCommand.NotifyCanExecuteChanged();
            }
        };
        RefreshCharacters();
    }

    /// <summary>セリフ入力欄にフォーカスを移す（画面が行う）</summary>
    public event EventHandler? FocusRequested;

    public ObservableCollection<Character> Characters { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(ListenCommand))]
    public partial Character? Character { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(ListenCommand))]
    public partial string Text { get; set; } = "";

    /// <summary>読み（音声記号列）。空なら本文から自動で作る</summary>
    [ObservableProperty]
    public partial string Reading { get; set; } = "";

    /// <summary>声を作っている間 true</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    public bool HasCharacters => Characters.Count > 0;

    [RelayCommand]
    private void Focus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>再生位置にセリフを置いて声を作る（置くのと声で 1 回の操作）</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (Character is not { } character || string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        var text = Text.Trim();
        var project = _session.Project;
        var sceneId = _session.SceneId;
        var start = _session.CurrentFrame;
        var layer = TargetLayer(project, sceneId, start);
        var operations = new List<IOperation>();
        if (layer.AddLayer is { } addLayer)
        {
            operations.Add(addLayer);
            project = addLayer.Apply(project).Project;
        }

        IOperation add;
        IReadOnlyList<ItemId> ids;
        try
        {
            (add, ids) = ScriptImport.CreateItems(project, sceneId, layer.Layer, start, [new ScriptLine(character.Id, text)]);
        }
        catch (EditException ex)
        {
            await _dialogs.ShowErrorAsync("セリフを置けません", ex.Message);
            return;
        }

        operations.Add(add);
        project = add.Apply(project).Project;
        var id = ids[0];
        if (Reading.Trim() is { Length: > 0 } reading)
        {
            var withReading = EditCommands.UpdateItem(project, sceneId, ((VoiceItem)project.GetScene(sceneId).GetItem(id)) with { Reading = reading });
            operations.Add(withReading);
            project = withReading.Apply(project).Project;
        }

        IsBusy = true;
        Status = "声を作っています…";
        try
        {
            var (voice, failures) = await VoiceEditing.SynthesizeAsync(project, sceneId, ids, _session.Voices);
            if (voice is not null)
            {
                operations.Add(voice);
                project = voice.Apply(project).Project;
            }

            Status = failures.Count > 0 ? $"声を作れませんでした：{failures[0].Message}" : "";
        }
        catch (EditException ex)
        {
            Status = $"長さを声に合わせられませんでした：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        if (_session.Execute(Operations.Combine("セリフの追加", operations)) is { } error)
        {
            await _dialogs.ShowErrorAsync("セリフを置けません", error);
            return;
        }

        _lastLayer = layer.Layer;
        var item = _session.Scene.GetItem(id);
        _session.Selection = [id];
        _session.Seek(item.End);
        Text = "";
        Reading = "";
    }

    /// <summary>入れている文を、置かずに鳴らす</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task ListenAsync()
    {
        if (Character is not { } character)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _session.Voices.GetAsync(character.Voice, Text.Trim(), Reading.Trim() is { Length: > 0 } reading ? reading : null);
            Status = $"読み：{result.Reading}";
            _player.Play(result.Path);
        }
        catch (VoiceEngineException ex)
        {
            Status = $"声を作れませんでした：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>選んだセリフの声を作り直す（本文や読みを変えたとき）</summary>
    [RelayCommand(CanExecute = nameof(HasVoiceSelection))]
    private async Task ResynthesizeAsync()
    {
        var ids = _session.SelectedItems.OfType<VoiceItem>().Select(item => item.Id).ToList();
        IsBusy = true;
        Status = "声を作っています…";
        try
        {
            var (operation, failures) = await VoiceEditing.SynthesizeAsync(_session.Project, _session.SceneId, ids, _session.Voices);
            Status = failures.Count > 0 ? $"{failures.Count} 個のセリフの声を作れませんでした：{failures[0].Message}" : "";
            if (operation is not null && _session.Execute(operation) is { } error)
            {
                await _dialogs.ShowErrorAsync("声を付けられません", error);
            }
        }
        catch (EditException ex)
        {
            await _dialogs.ShowErrorAsync("声を付けられません", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>台本を貼り付けて、再生位置から順にセリフを置き、声を作る。「名前：セリフ」の行は、そのキャラクターのセリフにする</summary>
    [RelayCommand]
    private async Task PasteScriptAsync()
    {
        if (Characters.Count == 0)
        {
            await _dialogs.ShowErrorAsync("台本の貼り付け", "先にキャラクターを作ってください");
            return;
        }

        var script = await _dialogs.PromptAsync(
            "台本の貼り付け",
            "1 行に 1 つのセリフ。「名前：セリフ」「名前「セリフ」」と書くと、そのキャラクターのセリフになります",
            "",
            multiline: true);
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        var first = Character ?? Characters[0];
        var lines = ScriptImport.Parse(script, _session.Project.Characters.Values.ToList(), first.Id);
        if (lines.Count == 0)
        {
            return;
        }

        var project = _session.Project;
        var sceneId = _session.SceneId;
        var start = _session.CurrentFrame;
        var operations = new List<IOperation>();
        var layer = TargetLayer(project, sceneId, start, lines.Count * 60L);
        if (layer.AddLayer is { } addLayer)
        {
            operations.Add(addLayer);
            project = addLayer.Apply(project).Project;
        }

        IOperation add;
        IReadOnlyList<ItemId> ids;
        try
        {
            (add, ids) = ScriptImport.CreateItems(project, sceneId, layer.Layer, start, lines);
        }
        catch (EditException ex)
        {
            await _dialogs.ShowErrorAsync("台本を貼り付けられません", ex.Message);
            return;
        }

        operations.Add(add);
        project = add.Apply(project).Project;
        IsBusy = true;
        Status = $"{lines.Count} 個のセリフの声を作っています…";
        try
        {
            var (voice, failures) = await VoiceEditing.SynthesizeAsync(project, sceneId, ids, _session.Voices);
            if (voice is not null)
            {
                operations.Add(voice);
                project = voice.Apply(project).Project;
            }

            // 声の長さに合わせて、セリフを間を空けずに並べ直す
            var cursor = start;
            foreach (var id in ids)
            {
                var item = project.GetScene(sceneId).GetItem(id);
                if (item.Start != cursor)
                {
                    var moved = EditCommands.UpdateItem(project, sceneId, item with { Start = cursor });
                    operations.Add(moved);
                    project = moved.Apply(project).Project;
                }

                cursor += item.Length;
            }

            Status = failures.Count > 0 ? $"{failures.Count} 個のセリフの声を作れませんでした：{failures[0].Message}" : $"{lines.Count} 個のセリフを置きました";
        }
        catch (EditException ex)
        {
            Status = $"長さを声に合わせられませんでした：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        if (_session.Execute(Operations.Combine("台本の貼り付け", operations)) is { } error)
        {
            await _dialogs.ShowErrorAsync("台本を貼り付けられません", error);
            return;
        }

        _lastLayer = layer.Layer;
        _session.Selection = [.. ids];
    }

    private bool CanAdd() => Character is not null && !string.IsNullOrWhiteSpace(Text);

    private bool HasVoiceSelection() => _session.SelectedItems.OfType<VoiceItem>().Any();

    // セリフを置くレイヤー：選んでいるセリフのレイヤー、前に置いたレイヤー、セリフのあるレイヤーの順に、空いていれば使う
    private (LayerId Layer, IOperation? AddLayer) TargetLayer(Project project, SceneId sceneId, long start, long length = 30)
    {
        var scene = project.GetScene(sceneId);
        var preferred = _session.SelectedItems.OfType<VoiceItem>().Select(item => (LayerId?)item.Layer).FirstOrDefault()
            ?? _lastLayer
            ?? scene.Items.Values.OfType<VoiceItem>().Select(item => (LayerId?)item.Layer).FirstOrDefault();
        var index = preferred is { } layer ? Math.Max(0, scene.LayerIndex(layer)) : 0;
        return MediaItems.FindFreeLayer(project, sceneId, start, length, index);
    }

    private void RefreshCharacters()
    {
        var selected = Character?.Id;
        var characters = _session.Project.Characters.Values.OrderBy(character => character.Name, StringComparer.CurrentCulture).ToList();
        if (characters.SequenceEqual(Characters))
        {
            return;
        }

        Characters.Clear();
        foreach (var character in characters)
        {
            Characters.Add(character);
        }

        Character = Characters.FirstOrDefault(character => character.Id == selected) ?? Characters.FirstOrDefault();
        OnPropertyChanged(nameof(HasCharacters));
    }
}
