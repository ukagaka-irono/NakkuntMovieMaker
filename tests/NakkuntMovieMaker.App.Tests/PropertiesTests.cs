using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Rendering.Effects;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>プロパティパネル：定義から作った入力欄での編集、キーフレーム、エフェクト、トランジション</summary>
public sealed class PropertiesTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private (EditorSession Session, PropertiesViewModel Properties) Create(Item item)
    {
        var session = _environment.CreateSession();
        item = item with { Layer = session.Scene.Layers[0].Id };
        Assert.Null(session.Execute(EditCommands.AddItem(session.Project, session.SceneId, item)));
        var properties = new PropertiesViewModel(session, _dialogs, ["Arial", "Meiryo"]);
        session.Selection = [item.Id];
        return (session, properties);
    }

    private static T Editor<T>(PropertiesViewModel properties, string group, string label)
        where T : PropertyEditor =>
        properties.Groups.Single(candidate => candidate.Title == group).Editors.OfType<T>().Single(editor => editor.Label == label);

    private static ShapeItem NewShape() =>
        new(ItemId.New(), default, 0, 60, new ShapeStyle(ShapeKind.Rectangle, 100, 50, new SolidPaint(RgbaColor.White)));

    [Fact]
    public Task BuildsGroupsFromDefinitions() => Ui.Run(() =>
    {
        var (session, properties) = Create(NewShape());
        using var _ = session;

        Assert.Equal(["タイミング", "位置・変形", "切り抜き", "図形", "トランジション（始まり）"], properties.Groups.Select(group => group.Title));
        Assert.Equal("四角形", properties.Title);
        Assert.True(properties.CanHaveEffects);

        session.Selection = [];
        Assert.False(properties.HasItem);
        Assert.Contains("アイテムを選ぶと", properties.Hint);
    });

    [Fact]
    public Task EditsValuesAsUndoableOperations() => Ui.Run(() =>
    {
        var (session, properties) = Create(NewShape());
        using var _ = session;
        var id = session.Selection.Single();

        Editor<AnimatedNumberEditor>(properties, "位置・変形", "X").Value = 120;
        Editor<NumberEditor>(properties, "図形", "幅").Value = 300;
        Editor<ChoiceEditor>(properties, "図形", "種類").Selected = Editor<ChoiceEditor>(properties, "図形", "種類").Choices.Single(choice => choice.Name == "星形");
        Editor<ColorEditor>(properties, "図形", "塗りの色").Text = "#FF000080";
        Editor<BoolEditor>(properties, "位置・変形", "左右反転").Value = true;

        var shape = (ShapeItem)session.Scene.GetItem(id);
        Assert.Equal(120, shape.Transform.X.Value);
        Assert.Equal((ShapeKind.Star, 300.0), (shape.Shape.Kind, shape.Shape.Width));
        Assert.Equal(new SolidPaint(new RgbaColor(255, 0, 0, 128)), shape.Shape.Fill);
        Assert.True(shape.Transform.FlipHorizontal);
        Assert.Equal("左右反転の変更", session.Document.UndoDescription);

        // 元に戻すと、入力欄も戻る
        session.Document.Undo();
        Assert.False(Editor<BoolEditor>(properties, "位置・変形", "左右反転").Value);

        // 重なる位置には動かせない
        var other = NewShape() with { Layer = session.Scene.Layers[0].Id, Start = 100 };
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, other));
        Editor<NumberEditor>(properties, "タイミング", "開始（フレーム）").Value = 80;
        Assert.Single(_dialogs.Errors);
        Assert.Equal(0, session.Scene.GetItem(id).Start);
        Assert.Equal(0, Editor<NumberEditor>(properties, "タイミング", "開始（フレーム）").Value);
    });

    [Fact]
    public Task SetsKeyframesAtThePlayhead() => Ui.Run(() =>
    {
        var (session, properties) = Create(NewShape());
        using var _ = session;
        var id = session.Selection.Single();
        Animatable<double> Opacity() => ((VisualItem)session.Scene.GetItem(id)).Opacity;

        session.CurrentFrame = 10;
        var opacity = Editor<AnimatedNumberEditor>(properties, "位置・変形", "不透明度");
        opacity.ToggleKeyCommand.Execute(null);
        Assert.True(opacity.HasKey);
        Assert.Equal([10L], Opacity().Keys.Select(key => key.Frame));

        // キーフレームがあれば、値を変えると再生位置にキーフレームを置く
        session.CurrentFrame = 30;
        Assert.False(opacity.HasKey);
        opacity.Value = 0;
        Assert.Equal([(10L, 1.0), (30L, 0.0)], Opacity().Keys.Select(key => (key.Frame, key.Value)));
        session.CurrentFrame = 20;
        Assert.Equal(0.5m, opacity.Value);

        session.CurrentFrame = 10;
        opacity.Easing = EasingChoice.All.Single(choice => choice.Name == "加速");
        Assert.Equal(new Easing(EasingCurve.Sine, EasingMode.In), Opacity().Keys[0].Easing);

        // 外す。最後の 1 つを外すとアニメーションしない値に戻る
        opacity.ToggleKeyCommand.Execute(null);
        session.CurrentFrame = 30;
        opacity.ToggleKeyCommand.Execute(null);
        Assert.False(Opacity().IsAnimated);
        Assert.Equal(0, Opacity().Value);
    });

    [Fact]
    public Task EditsTextAndStyles() => Ui.Run(() =>
    {
        var (session, properties) = Create(new TextItem(ItemId.New(), default, 0, 30, "前"));
        using var _ = session;
        var id = session.Selection.Single();

        var text = Editor<TextEditor>(properties, "テキスト", "本文");
        text.Text = "後";
        Assert.Equal("前", ((TextItem)session.Scene.GetItem(id)).Text); // 入力し終えるまでは変えない
        text.Commit();
        Editor<NumberEditor>(properties, "縁取り・影・背景", "縁取りの太さ").Value = 0;
        Editor<BoolEditor>(properties, "縁取り・影・背景", "背景").Value = true;
        var font = Editor<ChoiceEditor>(properties, "テキスト", "フォント");
        font.Selected = font.Choices.Single(choice => choice.Name == "Meiryo");

        var item = (TextItem)session.Scene.GetItem(id);
        Assert.Equal("後", item.Text);
        Assert.Null(item.Style.Outline);
        Assert.NotNull(item.Style.Background);
        Assert.Equal("Meiryo", item.Style.FontFamily);
        Assert.Equal("後", properties.Title);
    });

    [Fact]
    public Task AddsAndArrangesEffects() => Ui.Run(() =>
    {
        var (session, properties) = Create(NewShape());
        using var _ = session;
        var id = session.Selection.Single();
        var blur = PropertiesViewModel.EffectDefinitions.Single(definition => definition.Type == "nmm.blur");
        var mono = PropertiesViewModel.EffectDefinitions.Single(definition => definition.Type == "nmm.monochrome");

        properties.AddEffectCommand.Execute(blur);
        properties.AddEffectCommand.Execute(mono);
        Assert.Equal([blur.Name, mono.Name], properties.Groups.Where(group => group.IsEffect).Select(group => group.Title));

        // パラメーターは定義から作り、既定値を表示する
        var radius = properties.Groups.Single(group => group.Title == blur.Name).Editors.OfType<AnimatedNumberEditor>().First();
        Assert.Equal((decimal)(double)blur.Parameters[0].Default, radius.Value);
        radius.Value = 12;
        Assert.Equal(12, ((NumberParameter)session.Scene.GetItem(id).Effects[0].Parameters[blur.Parameters[0].Name]).Value.Value);

        var monoEffect = session.Scene.GetItem(id).Effects[1];
        properties.MoveEffectUpCommand.Execute(monoEffect);
        Assert.Equal(["nmm.monochrome", "nmm.blur"], session.Scene.GetItem(id).Effects.Select(effect => effect.Type));
        properties.ToggleEffectCommand.Execute(monoEffect);
        Assert.False(session.Scene.GetItem(id).Effects[0].Enabled);
        Assert.EndsWith("（無効）", properties.Groups.First(group => group.IsEffect).Title);
        properties.RemoveEffectCommand.Execute(monoEffect);
        Assert.Equal(["nmm.blur"], session.Scene.GetItem(id).Effects.Select(effect => effect.Type));
    });

    [Fact]
    public Task SetsTransitions() => Ui.Run(() =>
    {
        var (session, properties) = Create(NewShape());
        using var _ = session;
        var id = session.Selection.Single();
        const string Group = "トランジション（始まり）";

        var type = Editor<ChoiceEditor>(properties, Group, "種類");
        type.Selected = type.Choices.Single(choice => (string)choice.Value == "nmm.wipe");
        Assert.Equal(("nmm.wipe", 15L), (session.Scene.GetItem(id).Transition!.Type, session.Scene.GetItem(id).Transition!.Duration));

        Editor<NumberEditor>(properties, Group, "長さ（フレーム）").Value = 30;
        var direction = Editor<ChoiceEditor>(properties, Group, "向き");
        direction.Selected = direction.Choices.Single(choice => choice.Name == "上から");
        var transition = session.Scene.GetItem(id).Transition!;
        Assert.Equal(30, transition.Duration);
        Assert.Equal(new TextParameter("fromTop"), transition.Parameters["direction"]);

        type = Editor<ChoiceEditor>(properties, Group, "種類");
        type.Selected = type.Choices.Single(choice => choice.Name == "なし");
        Assert.Null(session.Scene.GetItem(id).Transition);
    });

    [Fact]
    public void ParsesColors()
    {
        Assert.True(ColorEditor.TryParse("#12AbEF", out var color));
        Assert.Equal(new RgbaColor(0x12, 0xAB, 0xEF), color);
        Assert.True(ColorEditor.TryParse("00000080", out color));
        Assert.Equal(new RgbaColor(0, 0, 0, 0x80), color);
        Assert.False(ColorEditor.TryParse("#123", out _));
        Assert.Equal("#FFFFFF", ColorEditor.Format(RgbaColor.White));
        Assert.Equal("#00000080", ColorEditor.Format(new RgbaColor(0, 0, 0, 128)));
    }

    [Fact]
    public void DefinesPropertiesForEveryKindOfItem()
    {
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);
        Item[] items =
        [
            new VideoItem(ItemId.New(), default, 0, 1, AssetId.New()),
            new ImageItem(ItemId.New(), default, 0, 1, AssetId.New()),
            new AudioItem(ItemId.New(), default, 0, 1, AssetId.New()),
            new TextItem(ItemId.New(), default, 0, 1, "a"),
            NewShape(),
            new VoiceItem(ItemId.New(), default, 0, 1, CharacterId.New(), "a"),
            new TachieItem(ItemId.New(), default, 0, 1, CharacterId.New()),
        ];

        foreach (var item in items)
        {
            var definitions = ItemProperties.For(item, project, []);
            Assert.All(definitions, definition => Assert.False(string.IsNullOrEmpty(definition.Label)));
            // すべての定義で、読んだ値をそのまま書くとアイテムが変わらない
            foreach (var definition in definitions)
            {
                var same = definition switch
                {
                    AnimatedNumberProperty p => p.Set(item, p.Get(item)),
                    NumberProperty p => p.Set(item, p.Get(item)),
                    ColorProperty p => p.Set(item, p.Get(item)),
                    BoolProperty p => p.Set(item, p.Get(item)),
                    ChoiceProperty p => p.Set(item, p.Get(item)),
                    TextProperty p => p.Set(item, p.Get(item)),
                    _ => item,
                };
                Assert.True(item.Equals(same) || definition.Label is "縁取りの色" or "影の色" or "影の位置 X" or "影の位置 Y" or "影のぼかし" or "背景の色" or "背景の余白" or "背景の角丸" or "グラデーションの終わりの色" or "線の色" or "線の太さ", $"{item.GetType().Name}: {definition.Label}");
            }
        }

        var effectsAll = EffectRegistry.Default.All.ToList();
        Assert.Equal(effectsAll.Count, PropertiesViewModel.EffectDefinitions.Count);
    }
}
