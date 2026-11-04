using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>キャラクターの設定と、音声合成エンジンの案内</summary>
public sealed class CharactersTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private (EditorSession Session, CharactersViewModel Characters, VoicePlayer Player) Create()
    {
        var session = _environment.CreateSession();
        var player = new VoicePlayer(disabled: true, () => null);
        return (session, new CharactersViewModel(session, _dialogs, player, ["Meiryo"]), player);
    }

    private static Character Only(EditorSession session) => session.Project.Characters.Values.Single();

    [Fact]
    public Task AddsAndEditsCharacters() => Ui.Run(async () =>
    {
        var (session, characters, player) = Create();
        using var _ = session;
        Assert.False(characters.HasSelection);

        characters.AddCommand.Execute(null);
        var created = Only(session);
        Assert.Equal(created, characters.Selected);
        Assert.Equal(new VoiceSpec("test", "a", VoiceParams.Default), created.Voice);
        Assert.Equal("test", characters.Engine!.Id);
        Assert.False(characters.HasEngineProblem);

        characters.Name = "霊夢";
        characters.CommitName();
        characters.Speed = 1.2m;
        var size = characters.SubtitleEditors.OfType<NumberEditor>().Single(editor => editor.Label == "大きさ");
        size.Value = 80;
        Assert.Equal(("霊夢", 1.2, 80.0), (Only(session).Name, Only(session).Voice.Params.Speed, Only(session).Subtitle.FontSize));
        Assert.Equal("字幕のスタイル", session.Document.UndoDescription);
        Assert.Equal(1.2m, characters.Speed); // 作り直しても選んだまま

        session.Document.Undo();
        Assert.Equal(56, Only(session).Subtitle.FontSize);
        Assert.Equal(56, size.Value);

        await characters.ListenCommand.ExecuteAsync(null);
        Assert.NotNull(player.LastPlayed);

        characters.DuplicateCommand.Execute(null);
        Assert.Equal(2, session.Project.Characters.Count);
        Assert.Equal("霊夢（コピー）", characters.Selected!.Name);
    });

    [Fact]
    public Task AsksBeforeRemovingCharactersInUse() => Ui.Run(async () =>
    {
        var (session, characters, _) = Create();
        using var _ = session;
        characters.AddCommand.Execute(null);
        var character = Only(session);
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, new VoiceItem(ItemId.New(), session.Scene.Layers[0].Id, 0, 30, character.Id, "あ")));

        _dialogs.Confirmations.Enqueue(false);
        await characters.RemoveCommand.ExecuteAsync(null);
        Assert.Single(session.Project.Characters);
        _dialogs.Confirmations.Enqueue(true);
        await characters.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(session.Project.Characters);
        Assert.False(characters.HasSelection);
    });

    [Fact]
    public Task SetsAndPlacesTachie() => Ui.Run(async () =>
    {
        var (session, characters, _) = Create();
        using var _ = session;
        characters.AddCommand.Execute(null);

        await characters.PlaceTachieCommand.ExecuteAsync(null);
        Assert.Single(_dialogs.Errors);

        characters.TachieType = CharactersViewModel.TachieKinds.Single(choice => choice.Kind == TachieKind.Simple);
        var image = Path.Combine(session.ProjectFolder, "tachie", "reimu.png");
        _dialogs.FilesToOpen.Enqueue(image);
        await characters.BrowseTachieCommand.ExecuteAsync(null);
        Assert.Equal(new TachieSpec(TachieKind.Simple, "tachie/reimu.png"), Only(session).Tachie);
        Assert.Equal("tachie/reimu.png", characters.TachiePath);

        session.CurrentFrame = 15;
        await characters.PlaceTachieCommand.ExecuteAsync(null);
        var tachie = Assert.IsType<TachieItem>(session.Scene.GetItem(session.Selection.Single()));
        Assert.Equal((15L, 300L, Only(session).Id), (tachie.Start, tachie.Length, tachie.Character));

        characters.TachieType = CharactersViewModel.TachieKinds[0];
        Assert.Null(Only(session).Tachie);
    });

    [Fact]
    public Task ListsTheBuiltInEngines() => Ui.Run(() =>
    {
        // 組み込みのエンジン：ColorfulTalk（同梱）と SAPI（Windows だけ）
        using var session = new EditorSession(_environment.Settings, disableAudioDevice: true, settingsPath: _environment.SettingsPath);
        var engines = new VoiceEnginesViewModel(session);

        Assert.Equal(["colorfultalk1", "sapi"], engines.Engines.Select(engine => engine.Id).Order(StringComparer.Ordinal));
        var colorful = engines.Engines.Single(engine => engine.Id == "colorfultalk1");
        Assert.Equal("ColorfulTalk（旧版）", colorful.Name);
        Assert.Contains("きょ'うわ/い'い/てんきです。", VoiceEnginesViewModel.KoeGuide);
    });
}
