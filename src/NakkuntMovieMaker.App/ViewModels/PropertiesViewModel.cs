using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Transitions;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>プロパティの見出しと、その中の入力欄。Effect が null でなければエフェクトの見出し（有効・並べ替え・削除ができる）</summary>
public sealed partial class PropertyGroup(string title, IReadOnlyList<PropertyEditor> editors, EffectInstance? effect = null) : ObservableObject
{
    public string Title { get; } = title;

    public IReadOnlyList<PropertyEditor> Editors { get; } = editors;

    public EffectInstance? Effect { get; } = effect;

    public bool IsEffect => Effect is not null;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>
/// プロパティパネル。選んだアイテムのプロパティを、定義（ItemProperties・エフェクトとトランジションの定義）から作った入力欄で編集する。
/// 値を変えるたびに 1 回の操作になる（元に戻せる）。キーフレームのある値は、再生位置のキーフレームを変える
/// </summary>
public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<string> _fonts;
    private string _layoutKey = "";

    public PropertiesViewModel(EditorSession session, IDialogService dialogs, IReadOnlyList<string>? fonts = null)
    {
        _session = session;
        _dialogs = dialogs;
        _fonts = fonts ?? SystemFonts();
        session.Changed += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorSession.Selection) or nameof(EditorSession.CurrentFrame))
            {
                Refresh();
            }
        };
        Refresh();
    }

    public ObservableCollection<PropertyGroup> Groups { get; } = [];

    /// <summary>編集しているアイテム（1 つだけ選んでいるとき）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    public partial Item? Item { get; private set; }

    public bool HasItem => Item is not null;

    /// <summary>何も選んでいない・複数選んでいるときの案内</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = "";

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    public bool CanHaveEffects => Item is VisualItem;

    public static IReadOnlyList<EffectDefinition> EffectDefinitions { get; } = [.. EffectRegistry.Default.All.OrderBy(definition => definition.Name, StringComparer.CurrentCulture)];

    /// <summary>エフェクトを足す</summary>
    [RelayCommand]
    private void AddEffect(EffectDefinition definition)
    {
        var effect = new EffectInstance(EffectId.New(), definition.Type, ImmutableDictionary<string, ParameterValue>.Empty);
        ApplyToItem(item => item with { Effects = item.Effects.Add(effect) }, $"エフェクト（{definition.Name}）の追加");
    }

    [RelayCommand]
    private void RemoveEffect(EffectInstance effect) =>
        ApplyToItem(item => item with { Effects = item.Effects.RemoveAll(candidate => candidate.Id == effect.Id) }, "エフェクトの削除");

    [RelayCommand]
    private void MoveEffectUp(EffectInstance effect) => MoveEffect(effect, -1);

    [RelayCommand]
    private void MoveEffectDown(EffectInstance effect) => MoveEffect(effect, 1);

    [RelayCommand]
    private void ToggleEffect(EffectInstance effect) =>
        ApplyToItem(item => item with { Effects = item.Effects.Select(candidate => candidate.Id == effect.Id ? candidate with { Enabled = !candidate.Enabled } : candidate).ToImmutableList() }, "エフェクトの有効・無効");

    // 選択・再生位置・プロジェクトが変わったら、入力欄を作り直すか、値を読み直す
    private void Refresh()
    {
        if (_session.IsClosed)
        {
            return;
        }

        var selection = _session.SelectedItems;
        if (selection.Count != 1)
        {
            Item = null;
            Groups.Clear();
            _layoutKey = "";
            Title = "";
            Hint = selection.Count == 0 ? "アイテムを選ぶと、ここでプロパティを変えられます" : $"{selection.Count} 個のアイテムを選んでいます";
            OnPropertyChanged(nameof(CanHaveEffects));
            return;
        }

        var item = selection[0];
        Item = item;
        Hint = "";
        Title = ItemLabels.Label(item, _session.Project) is { Length: > 0 } label ? label : item.GetType().Name;
        var key = LayoutKey(item);
        if (key != _layoutKey)
        {
            _layoutKey = key;
            Build(item);
        }

        var local = Math.Clamp(_session.CurrentFrame - item.Start, 0, Math.Max(0, item.Length - 1));
        foreach (var editor in Groups.SelectMany(group => group.Editors))
        {
            editor.Refresh(item, local);
        }

        OnPropertyChanged(nameof(CanHaveEffects));
    }

    // 入力欄の並びが変わる要素（アイテム・種類・エフェクトの並び・トランジションの有無・キャラクター）
    private string LayoutKey(Item item) =>
        $"{item.Id}|{item.GetType().Name}|{string.Join(",", item.Effects.Select(effect => $"{effect.Id}:{effect.Enabled}"))}|{item.Transition?.Type}|{_session.Project.Characters.Count}"
        + (item is ShapeItem shape ? $"|{shape.Shape.Fill.GetType().Name}" : "");

    private void Build(Item item)
    {
        var expanded = Groups.ToDictionary(group => group.Title, group => group.IsExpanded);
        Groups.Clear();

        var definitions = ItemProperties.For(item, _session.Project, _fonts).ToList();
        if (item is VisualItem)
        {
            definitions.AddRange(TransitionProperties(item));
        }

        foreach (var group in definitions.GroupBy(definition => definition.Group))
        {
            Groups.Add(new PropertyGroup(group.Key, [.. group.Select(CreateEditor)]) { IsExpanded = expanded.GetValueOrDefault(group.Key, true) });
        }

        for (var index = 0; index < item.Effects.Count; index++)
        {
            var effect = item.Effects[index];
            var definition = EffectRegistry.Default.Find(effect.Type);
            var title = (definition?.Name ?? effect.Type) + (effect.Enabled ? "" : "（無効）");
            var editors = definition is null ? [] : EffectProperties(effect.Id, definition).Select(CreateEditor).ToList();
            Groups.Add(new PropertyGroup(title, editors, effect) { IsExpanded = expanded.GetValueOrDefault(title, true) });
        }
    }

    private PropertyEditor CreateEditor(PropertyDefinition definition) => definition switch
    {
        AnimatedNumberProperty animated => new AnimatedNumberEditor(animated, ApplyToItem),
        NumberProperty number => new NumberEditor(number, ApplyToItem),
        ColorProperty color => new ColorEditor(color, ApplyToItem),
        BoolProperty boolean => new BoolEditor(boolean, ApplyToItem),
        ChoiceProperty choice => new ChoiceEditor(choice, ApplyToItem),
        TextProperty text => new TextEditor(text, ApplyToItem),
        FileProperty file => new FileEditor(file, ApplyToItem, _dialogs),
        _ => throw new NotSupportedException(definition.GetType().Name),
    };

    // 選んでいるアイテムを変える。変わらなければ何もしない
    private async void ApplyToItem(Func<Item, Item> change, string description)
    {
        if (Item is not { } current || !_session.Scene.Items.TryGetValue(current.Id, out var item))
        {
            return;
        }

        var changed = change(item);
        if (changed.Equals(item))
        {
            return;
        }

        IOperation operation;
        try
        {
            operation = EditCommands.UpdateItem(_session.Project, _session.SceneId, changed);
        }
        catch (EditException ex)
        {
            await _dialogs.ShowErrorAsync("変更できません", ex.Message);
            Refresh();
            return;
        }

        if (_session.Execute(Operations.Combine(description, [operation])) is { } error)
        {
            await _dialogs.ShowErrorAsync("変更できません", error);
            Refresh();
        }
    }

    private void MoveEffect(EffectInstance effect, int delta) => ApplyToItem(
        item =>
        {
            var index = item.Effects.FindIndex(candidate => candidate.Id == effect.Id);
            var target = index + delta;
            return index < 0 || target < 0 || target >= item.Effects.Count
                ? item
                : item with { Effects = item.Effects.RemoveAt(index).Insert(target, item.Effects[index]) };
        },
        "エフェクトの並べ替え");

    // エフェクトのパラメーターを、アイテムのプロパティとして扱う（ID でエフェクトを探す）
    private static IEnumerable<PropertyDefinition> EffectProperties(EffectId id, EffectDefinition definition)
    {
        var group = definition.Name;
        EffectInstance Find(Item item) => item.Effects.First(effect => effect.Id == id);
        Item With(Item item, string name, ParameterValue value) =>
            item with { Effects = item.Effects.Select(effect => effect.Id == id ? effect with { Parameters = effect.Parameters.SetItem(name, value) } : effect).ToImmutableList() };

        foreach (var parameter in definition.Parameters)
        {
            var name = parameter.Name;
            yield return ParameterProperty(group, parameter, item => Find(item).Parameters.GetValueOrDefault(name), (item, value) => With(item, name, value));
        }
    }

    private static IEnumerable<PropertyDefinition> TransitionProperties(Item item)
    {
        const string Group = "トランジション（始まり）";
        IReadOnlyList<(object, string)> choices = [("", "なし"), .. TransitionRegistry.Default.All.Select(definition => ((object)definition.Type, definition.Name))];
        yield return new ChoiceProperty(Group, "種類", choices, i => i.Transition?.Type ?? "", (i, value) => i with
        {
            Transition = (string)value is { Length: > 0 } type ? i.Transition is { } existing ? existing with { Type = type } : new TransitionInstance(type, 15) : null,
        });

        if (item.Transition is not { } transition || TransitionRegistry.Default.Find(transition.Type) is not { } definition)
        {
            yield break;
        }

        yield return new NumberProperty(Group, "長さ（フレーム）", i => i.Transition?.Duration ?? 15, (i, value) => i with
        {
            Transition = i.Transition! with { Duration = Math.Max(1, (long)Math.Round(value)) },
        }, 1, 100000, 1, "0");

        foreach (var parameter in definition.Parameters)
        {
            var name = parameter.Name;
            yield return ParameterProperty(Group, parameter, i => i.Transition?.Parameters.GetValueOrDefault(name), (i, value) => i with
            {
                Transition = i.Transition! with { Parameters = i.Transition.Parameters.SetItem(name, value) },
            });
        }
    }

    // パラメーターの定義から、そのパラメーターを読み書きするプロパティを作る（値がなければ既定値）
    private static PropertyDefinition ParameterProperty(string group, ParameterDefinition parameter, Func<Item, ParameterValue?> get, Func<Item, ParameterValue, Item> set) =>
        parameter.Kind switch
        {
            ParameterKind.Number => new AnimatedNumberProperty(
                group, parameter.Label,
                item => get(item) is NumberParameter number ? number.Value : Animatable<double>.Constant((double)parameter.Default),
                (item, value) => set(item, new NumberParameter(value)),
                parameter.Min, parameter.Max, Increment(parameter)),
            ParameterKind.Color => new ColorProperty(
                group, parameter.Label,
                item => get(item) is ColorParameter color ? color.Value.Value : (RgbaColor)parameter.Default,
                (item, value) => set(item, new ColorParameter(Animatable<RgbaColor>.Constant(value)))),
            ParameterKind.Bool => new BoolProperty(
                group, parameter.Label,
                item => get(item) is BoolParameter boolean ? boolean.Value : (bool)parameter.Default,
                (item, value) => set(item, new BoolParameter(value))),
            ParameterKind.Choice => new ChoiceProperty(
                group, parameter.Label, [.. parameter.Choices.Select(choice => ((object)choice, ChoiceName(choice)))],
                item => get(item) is TextParameter text ? text.Value : (string)parameter.Default,
                (item, value) => set(item, new TextParameter((string)value))),
            _ => new FileProperty(
                group, parameter.Label, [FileKinds.Lut, FileKinds.Image, FileKinds.All],
                item => get(item) is TextParameter text ? text.Value : "",
                (item, value) => set(item, new TextParameter(value))),
        };

    private static double Increment(ParameterDefinition parameter) =>
        double.IsFinite(parameter.Max) && double.IsFinite(parameter.Min) && parameter.Max - parameter.Min <= 2 ? 0.05 : 1;

    // エフェクト・トランジションの選択肢の表示名
    private static string ChoiceName(string choice) => choice switch
    {
        "fromLeft" => "左から",
        "fromRight" => "右から",
        "fromTop" => "上から",
        "fromBottom" => "下から",
        "rectangle" => "四角形",
        "ellipse" => "円",
        _ => choice,
    };

    /// <summary>PC に入っているフォントの名前</summary>
    public static IReadOnlyList<string> SystemFonts()
    {
        try
        {
            return [.. FontManager.Current.SystemFonts.Select(family => family.Name).Distinct().Order(StringComparer.CurrentCulture)];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }
}
