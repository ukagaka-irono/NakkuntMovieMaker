using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>テキスト・図形の追加、テキストのプリセット、字幕（SRT）の読み込みと書き出し</summary>
public sealed class ItemsPresetsAndSubtitlesTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private MainWindowViewModel CreateViewModel() =>
        new(_environment.CreateSession(), new ShortcutRegistry(), _dialogs, recoveryFolder: _environment.RecoveryFolder, settingsPath: _environment.SettingsPath);

    [Fact]
    public Task AddsTextAndShapesAtThePlayhead() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var timeline = viewModel.Timeline;
        session.CurrentFrame = 30;

        await timeline.AddTextCommand.ExecuteAsync(null);
        var text = Assert.IsType<TextItem>(session.SelectedItems.Single());
        Assert.Equal((30L, 150L, "テキスト", TextStyle.Default), (text.Start, text.Length, text.Text, text.Style));
        Assert.Equal("テキストの追加", session.Document.UndoDescription);

        // プリセットを選ぶと、そのスタイル。空いているレイヤー（選んでいるアイテムの上）に置く
        var title = TextPresets.BuiltIn.Single(preset => preset.Name.StartsWith("タイトル", StringComparison.Ordinal));
        await timeline.AddTextCommand.ExecuteAsync(title);
        var second = Assert.IsType<TextItem>(session.SelectedItems.Single());
        Assert.Equal(title.Style, second.Style);
        Assert.Equal(session.Scene.LayerIndex(text.Layer) + 1, session.Scene.LayerIndex(second.Layer));

        await timeline.AddShapeCommand.ExecuteAsync(TimelineViewModel.ShapeChoices.Single(choice => choice.Kind == ShapeKind.Star));
        Assert.Equal(ShapeKind.Star, Assert.IsType<ShapeItem>(session.SelectedItems.Single()).Shape.Kind);

        // 背景はシーンの大きさで、いちばん下の空いているレイヤー
        await timeline.AddShapeCommand.ExecuteAsync(TimelineViewModel.ShapeChoices.Single(choice => choice.Kind == ShapeKind.Background));
        var background = Assert.IsType<ShapeItem>(session.SelectedItems.Single());
        Assert.Equal((1920.0, 1080.0), (background.Shape.Width, background.Shape.Height));
        Assert.Equal(3, session.Scene.LayerIndex(background.Layer)); // 0・1 はテキスト、2 は星が使っている
    });

    [Fact]
    public Task AppliesAndSavesTextPresets() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        await viewModel.Timeline.AddTextCommand.ExecuteAsync(null);
        var properties = viewModel.Properties;
        Assert.True(properties.IsText);
        var id = session.SelectedItems.Single().Id;

        properties.SelectedPreset = properties.Presets.Single(preset => preset.Name.StartsWith("テロップ", StringComparison.Ordinal));
        properties.ApplyPresetCommand.Execute(null);
        Assert.Equal(new RgbaColor(255, 220, 60), ((TextItem)session.Scene.GetItem(id)).Style.Color);
        Assert.False(properties.RemovePresetCommand.CanExecute(null)); // 組み込みは消せない

        // 今のスタイルを保存すると、メニューにも出る。設定ファイルに書き、読み直しても残る
        Editor<NumberEditor>(properties, "大きさ").Value = 50;
        _dialogs.Prompts.Enqueue("小さいテロップ");
        await properties.SavePresetCommand.ExecuteAsync(null);
        Assert.Equal("小さいテロップ", properties.SelectedPreset!.Name);
        Assert.Contains(viewModel.TextPresetList, preset => preset.Name == "小さいテロップ");
        var saved = AppSettings.Load(_environment.SettingsPath).TextPresets.Single();
        Assert.Equal(("小さいテロップ", 50.0, new RgbaColor(255, 220, 60)), (saved.Name, saved.Style.FontSize, saved.Style.Color));

        // 組み込みと同じ名前は使えない
        _dialogs.Prompts.Enqueue("縦書き");
        await properties.SavePresetCommand.ExecuteAsync(null);
        Assert.Single(_dialogs.Errors);

        properties.RemovePresetCommand.Execute(null);
        Assert.DoesNotContain(properties.Presets, preset => preset.Name == "小さいテロップ");
        Assert.Empty(AppSettings.Load(_environment.SettingsPath).TextPresets);
    });

    [Fact]
    public Task ImportsAndExportsSubtitles() => Ui.Run(async () =>
    {
        using var viewModel = CreateViewModel();
        var session = viewModel.Session;
        var srt = Path.Combine(_environment.Folder, "字幕.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:02,000\nこんにちは\n\n2\n00:00:02,500 --> 00:00:04,000\nさようなら\n", TestContext.Current.CancellationToken);
        var layers = session.Scene.Layers.Count;

        // キャラクターを選んでいなければ、テキストとして新しいレイヤーに読み込む
        _dialogs.FilesToOpen.Enqueue(srt);
        await viewModel.ImportSubtitlesCommand.ExecuteAsync(null);
        Assert.Equal(layers + 1, session.Scene.Layers.Count);
        var texts = session.Scene.Items.Values.OfType<TextItem>().OrderBy(item => item.Start).ToList();
        Assert.Equal([(30L, "こんにちは"), (75L, "さようなら")], texts.Select(item => (item.Start, item.Text)));
        Assert.Equal(TextPresets.BuiltIn[0].Style, texts[0].Style);

        // キャラクターを選んでいれば、セリフとして（再生位置を 0 秒にして）読み込む
        var character = new Character(CharacterId.New(), "霊夢", new VoiceSpec("test", "a", VoiceParams.Default), TextStyle.Default);
        session.Execute(new SetCharacter(character.Id, character));
        viewModel.Dialogue.Character = viewModel.Dialogue.Characters.Single();
        session.CurrentFrame = 300;
        _dialogs.FilesToOpen.Enqueue(srt);
        await viewModel.ImportSubtitlesCommand.ExecuteAsync(null);
        var voices = session.Scene.Items.Values.OfType<VoiceItem>().OrderBy(item => item.Start).ToList();
        Assert.Equal([330L, 375L], voices.Select(item => item.Start));
        Assert.All(voices, voice => Assert.Equal(character.Id, voice.Character));

        // 書き出し（範囲を指定していれば、その範囲）
        var output = Path.Combine(_environment.Folder, "out.srt");
        session.RangeStart = 300;
        _dialogs.FilesToSave.Enqueue(output);
        await viewModel.ExportSubtitlesCommand.ExecuteAsync(null);
        var cues = Engine.Subtitles.Srt.ReadFile(output);
        Assert.Equal([(TimeSpan.FromSeconds(1), "こんにちは"), (TimeSpan.FromSeconds(2.5), "さようなら")], cues.Select(cue => (cue.Start, cue.Text)));
    });

    [Fact]
    public void SavesTextPresetsInSettings()
    {
        var settings = new AppSettings();
        var presets = new TextPresets(settings);
        var style = TextStyle.Default with { FontSize = 33, Shadow = new TextShadow(new RgbaColor(1, 2, 3, 4), 5, 6, 7), Background = new TextBackground(RgbaColor.White, 9, 10) };

        presets.Save(" 自分用 ", style);
        presets.Save("自分用", style with { Bold = true });
        var path = Path.Combine(_environment.Folder, "s.json");
        settings.Save(path);

        var loaded = AppSettings.Load(path).TextPresets.Single();
        Assert.Equal(("自分用", style with { Bold = true }), (loaded.Name, loaded.Style));
        Assert.Throws<ArgumentException>(() => presets.Save(" ", style));
        Assert.Equal(TextPresets.BuiltIn.Count + 1, presets.All.Count);
    }

    private static T Editor<T>(PropertiesViewModel properties, string label)
        where T : PropertyEditor =>
        properties.Groups.SelectMany(group => group.Editors).OfType<T>().First(editor => editor.Label == label);
}
