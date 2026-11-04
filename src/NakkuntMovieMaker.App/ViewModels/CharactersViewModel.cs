using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>音声合成エンジンの選択肢と、使えるかどうか</summary>
public sealed record EngineChoice(string Id, string Name, bool Available, string? Message, string? HelpUrl)
{
    public override string ToString() => Available ? Name : $"{Name}（未導入）";
}

/// <summary>立ち絵の種類の選択肢</summary>
public sealed record TachieChoice(TachieKind? Kind, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// キャラクターの設定。キャラクターの追加・複製・削除と、名前・声（エンジン・声・速さ・高さ・抑揚・音量）・字幕のスタイル・立ち絵。
/// 変更はそれぞれ 1 回の操作（元に戻せる）。使えないエンジン（ColorfulTalk のライブラリがない、Windows 以外の SAPI など）は、理由を出す
/// </summary>
public sealed partial class CharactersViewModel : ObservableObject
{
    private const string SampleText = "こんにちは。ゆっくりしていってね";

    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private readonly VoicePlayer _player;
    private readonly IReadOnlyList<string> _fonts;
    private bool _loading;

    public CharactersViewModel(EditorSession session, IDialogService dialogs, VoicePlayer player, IReadOnlyList<string> fonts)
    {
        _session = session;
        _dialogs = dialogs;
        _player = player;
        _fonts = fonts;
        session.Changed += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        RefreshEngines();
        Refresh();
    }

    /// <summary>エンジンの導入の案内を開く（画面が行う）</summary>
    public event EventHandler? EngineGuideRequested;

    public ObservableCollection<Character> Characters { get; } = [];

    public ObservableCollection<EngineChoice> Engines { get; } = [];

    public ObservableCollection<VoiceInfo> Voices { get; } = [];

    public static IReadOnlyList<TachieChoice> TachieKinds { get; } =
    [
        new(null, "なし"),
        new(TachieKind.Simple, "画像 1 枚"),
        new(TachieKind.Parts, "パーツ式（フォルダ）"),
    ];

    /// <summary>字幕のスタイルの入力欄（テキストのアイテムと同じ定義から作る）</summary>
    public ObservableCollection<PropertyEditor> SubtitleEditors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(DuplicateCommand), nameof(ListenCommand), nameof(PlaceTachieCommand), nameof(BrowseTachieCommand))]
    public partial Character? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial EngineChoice? Engine { get; set; }

    [ObservableProperty]
    public partial VoiceInfo? Voice { get; set; }

    [ObservableProperty]
    public partial decimal Speed { get; set; } = 1;

    [ObservableProperty]
    public partial decimal Pitch { get; set; } = 1;

    [ObservableProperty]
    public partial decimal Intonation { get; set; } = 1;

    [ObservableProperty]
    public partial decimal Volume { get; set; } = 1;

    [ObservableProperty]
    public partial TachieChoice? TachieType { get; set; }

    [ObservableProperty]
    public partial string TachiePath { get; set; } = "";

    /// <summary>選んだエンジンを使えないときの理由（使えれば null）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEngineProblem))]
    public partial string? EngineMessage { get; private set; }

    public bool HasEngineProblem => EngineMessage is not null;

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [RelayCommand]
    private void Add()
    {
        var engine = Engines.FirstOrDefault(choice => choice.Available) ?? Engines.FirstOrDefault();
        var voice = engine is null ? "" : _session.VoiceEngines.Find(engine.Id)?.Voices.FirstOrDefault()?.Id ?? "";
        var character = new Character(
            CharacterId.New(),
            $"キャラクター {Characters.Count + 1}",
            new VoiceSpec(engine?.Id ?? "sapi", voice, VoiceParams.Default),
            TextStyle.Default with { FontSize = 56 });
        _session.Execute(new SetCharacter(character.Id, character));
        Selected = Characters.FirstOrDefault(candidate => candidate.Id == character.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (Selected is not { } source)
        {
            return;
        }

        var copy = source with { Id = CharacterId.New(), Name = source.Name + "（コピー）" };
        _session.Execute(new SetCharacter(copy.Id, copy));
        Selected = Characters.FirstOrDefault(candidate => candidate.Id == copy.Id);
    }

    /// <summary>キャラクターを消す（セリフと立ち絵で使っていれば、確かめてから）</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        if (Selected is not { } character)
        {
            return;
        }

        var uses = _session.Project.Scenes.Values.SelectMany(scene => scene.Items.Values)
            .Count(item => item is VoiceItem voice && voice.Character == character.Id || item is TachieItem tachie && tachie.Character == character.Id);
        if (uses > 0 && !await _dialogs.ConfirmAsync("キャラクターの削除", $"「{character.Name}」は {uses} 個のセリフ・立ち絵で使っています。削除すると、それらは声と字幕のスタイルを失います。削除しますか？", "削除"))
        {
            return;
        }

        _session.Execute(new SetCharacter(character.Id, null));
    }

    /// <summary>今の声で見本の文を読ませる</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ListenAsync()
    {
        if (Selected is not { } character)
        {
            return;
        }

        try
        {
            var result = await _session.Voices.GetAsync(character.Voice, SampleText, null);
            _player.Play(result.Path);
            Status = "";
        }
        catch (VoiceEngineException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>立ち絵を、再生位置からシーンの終わりまで（短ければ 10 秒）空いているレイヤーに置く</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlaceTachieAsync()
    {
        if (Selected is not { } character)
        {
            return;
        }

        if (character.Tachie is null)
        {
            await _dialogs.ShowErrorAsync("立ち絵を置けません", "先に立ち絵の画像かフォルダを指定してください");
            return;
        }

        var scene = _session.Scene;
        var start = _session.CurrentFrame;
        var length = Math.Max(scene.Duration - start, (long)Math.Round(scene.Fps.ToDouble() * 10));
        var (layer, addLayer) = MediaItems.FindFreeLayer(_session.Project, _session.SceneId, start, length);
        var item = new TachieItem(ItemId.New(), layer, start, length, character.Id)
        {
            Transform = Transform2D.Identity with { X = scene.Format.Width * 0.3, Y = scene.Format.Height * 0.1 },
        };
        var project = addLayer?.Apply(_session.Project).Project ?? _session.Project;
        var operations = new List<IOperation>();
        if (addLayer is not null)
        {
            operations.Add(addLayer);
        }

        operations.Add(EditCommands.AddItem(project, _session.SceneId, item));
        if (_session.Execute(Operations.Combine("立ち絵の追加", operations)) is { } error)
        {
            await _dialogs.ShowErrorAsync("立ち絵を置けません", error);
            return;
        }

        _session.Selection = [item.Id];
    }

    /// <summary>立ち絵の画像（画像 1 枚）かフォルダ（パーツ式）を選ぶ</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task BrowseTachieAsync()
    {
        var parts = TachieType?.Kind == TachieKind.Parts;
        var path = parts
            ? await _dialogs.PickFolderAsync("パーツ式の立ち絵のフォルダ（体・目・口などのフォルダが入ったもの）")
            : await _dialogs.PickOpenFileAsync("立ち絵の画像", [FileKinds.Image]);
        if (path is null || Selected is not { } character)
        {
            return;
        }

        var stored = AssetLinks.ToStoredPath(path, _session.ProjectFolder);
        Update(character with { Tachie = new TachieSpec(parts ? TachieKind.Parts : TachieKind.Simple, stored) }, "立ち絵の設定");
    }

    /// <summary>エンジンの導入の案内を開く</summary>
    [RelayCommand]
    private void ShowEngineGuide() => EngineGuideRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>名前を入れ終えた（Enter・フォーカスが外れた）</summary>
    public void CommitName()
    {
        if (Selected is { } character && !string.IsNullOrWhiteSpace(Name) && Name.Trim() != character.Name)
        {
            Update(character with { Name = Name.Trim() }, "キャラクターの名前");
        }
    }

    partial void OnSelectedChanged(Character? value) => Load();

    partial void OnEngineChanged(EngineChoice? value)
    {
        Voices.Clear();
        foreach (var voice in value is null ? [] : _session.VoiceEngines.Find(value.Id)?.Voices ?? [])
        {
            Voices.Add(voice);
        }

        EngineMessage = value is { Available: false } ? value.Message ?? "このエンジンは使えません" : null;
        if (!_loading && Selected is { } character && value is not null && value.Id != character.Voice.EngineId)
        {
            Update(character with { Voice = new VoiceSpec(value.Id, Voices.FirstOrDefault()?.Id ?? "", character.Voice.Params) }, "声の設定");
        }
    }

    partial void OnVoiceChanged(VoiceInfo? value)
    {
        if (!_loading && Selected is { } character && value is not null && value.Id != character.Voice.VoiceId)
        {
            Update(character with { Voice = character.Voice with { VoiceId = value.Id } }, "声の設定");
        }
    }

    partial void OnSpeedChanged(decimal value) => UpdateParams(parameters => parameters with { Speed = (double)value });

    partial void OnPitchChanged(decimal value) => UpdateParams(parameters => parameters with { Pitch = (double)value });

    partial void OnIntonationChanged(decimal value) => UpdateParams(parameters => parameters with { Intonation = (double)value });

    partial void OnVolumeChanged(decimal value) => UpdateParams(parameters => parameters with { Volume = (double)value });

    partial void OnTachieTypeChanged(TachieChoice? value)
    {
        if (_loading || Selected is not { } character || value is null)
        {
            return;
        }

        var tachie = value.Kind is { } kind ? new TachieSpec(kind, character.Tachie?.Path ?? "") : null;
        Update(character with { Tachie = tachie }, "立ち絵の設定");
    }

    private void UpdateParams(Func<VoiceParams, VoiceParams> change)
    {
        if (!_loading && Selected is { } character)
        {
            Update(character with { Voice = character.Voice with { Params = change(character.Voice.Params) } }, "声の調整");
        }
    }

    private void Update(Character character, string description) =>
        _session.Execute(Operations.Combine(description, [new SetCharacter(character.Id, character)]));

    private void RefreshEngines()
    {
        Engines.Clear();
        foreach (var engine in _session.VoiceEngines.All)
        {
            var status = engine.Status;
            Engines.Add(new EngineChoice(engine.Id, engine.Name, status.Available, status.Message, status.HelpUrl));
        }
    }

    private void Refresh()
    {
        if (_session.IsClosed)
        {
            return;
        }

        var selected = Selected?.Id;
        var characters = _session.Project.Characters.Values.OrderBy(character => character.Name, StringComparer.CurrentCulture).ToList();
        if (!characters.SequenceEqual(Characters))
        {
            Characters.Clear();
            foreach (var character in characters)
            {
                Characters.Add(character);
            }
        }

        var current = Characters.FirstOrDefault(character => character.Id == selected) ?? (selected is null ? Characters.FirstOrDefault() : null);
        if (!Equals(current, Selected))
        {
            Selected = current;
        }
        else
        {
            Load();
        }
    }

    // 選んだキャラクターの値を入力欄に読み込む（読み込んでいる間は変更として扱わない）
    private void Load()
    {
        _loading = true;
        try
        {
            if (Selected is not { } character)
            {
                SubtitleEditors.Clear();
                return;
            }

            Name = character.Name;
            Engine = Engines.FirstOrDefault(choice => choice.Id == character.Voice.EngineId);
            Voice = Voices.FirstOrDefault(voice => voice.Id == character.Voice.VoiceId);
            Speed = (decimal)character.Voice.Params.Speed;
            Pitch = (decimal)character.Voice.Params.Pitch;
            Intonation = (decimal)character.Voice.Params.Intonation;
            Volume = (decimal)character.Voice.Params.Volume;
            TachieType = TachieKinds.First(choice => choice.Kind == character.Tachie?.Kind);
            TachiePath = character.Tachie?.Path ?? "";

            // 字幕のスタイルは、スタイルを持たせた仮のテキストのアイテムで、テキストと同じ定義を使って編集する
            var subtitle = new TextItem(default, default, 0, 1, "") { Style = character.Subtitle };
            if (SubtitleEditors.Count == 0)
            {
                foreach (var definition in ItemProperties.StyleProperties(_fonts, item => ((TextItem)item).Style, (item, style) => ((TextItem)item) with { Style = style }))
                {
                    SubtitleEditors.Add(CreateEditor(definition));
                }
            }

            foreach (var editor in SubtitleEditors)
            {
                editor.Refresh(subtitle, 0);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private PropertyEditor CreateEditor(PropertyDefinition definition)
    {
        void Apply(Func<Item, Item> change, string description)
        {
            if (Selected is { } character)
            {
                var style = ((TextItem)change(new TextItem(default, default, 0, 1, "") { Style = character.Subtitle })).Style;
                if (style != character.Subtitle)
                {
                    Update(character with { Subtitle = style }, "字幕のスタイル");
                }
            }
        }

        return definition switch
        {
            NumberProperty number => new NumberEditor(number, Apply),
            ColorProperty color => new ColorEditor(color, Apply),
            BoolProperty boolean => new BoolEditor(boolean, Apply),
            ChoiceProperty choice => new ChoiceEditor(choice, Apply),
            _ => throw new NotSupportedException(definition.GetType().Name),
        };
    }
}
