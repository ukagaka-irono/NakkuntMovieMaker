using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>セリフ入力欄（置く・読み・試聴・作り直し・台本）と録音（タイムラインへ・セリフごと・録り直し）</summary>
public sealed class DialogueAndRecordingTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    private static Character AddCharacter(EditorSession session, string name)
    {
        var character = new Character(CharacterId.New(), name, new VoiceSpec("test", "a", VoiceParams.Default), TextStyle.Default);
        session.Execute(new SetCharacter(character.Id, character));
        return character;
    }

    private (EditorSession Session, DialogueViewModel Dialogue, VoicePlayer Player) CreateDialogue()
    {
        var session = _environment.CreateSession();
        var player = new VoicePlayer(disabled: true, () => null);
        return (session, new DialogueViewModel(session, _dialogs, player), player);
    }

    [Fact]
    public Task PlacesLinesAndContinuesFromTheirEnd() => Ui.Run(async () =>
    {
        var (session, dialogue, _) = CreateDialogue();
        using var _ = session;
        Assert.False(dialogue.HasCharacters);
        var reimu = AddCharacter(session, "霊夢");
        Assert.Equal(reimu, dialogue.Character);
        Assert.False(dialogue.AddCommand.CanExecute(null));

        session.CurrentFrame = 30;
        dialogue.Text = "ゆっくり";
        await dialogue.AddCommand.ExecuteAsync(null);

        var first = Assert.IsType<VoiceItem>(session.Scene.GetItem(session.Selection.Single()));
        Assert.Equal((30L, "ゆっくり", reimu.Id), (first.Start, first.Text, first.Character));
        Assert.Equal(12, first.Length); // 4 文字 × 0.1 秒 = 0.4 秒
        Assert.True(File.Exists(session.Voices.Cache.PathOf(((SynthesizedVoice)first.Source).CacheKey!)));
        Assert.Equal(first.End, session.CurrentFrame);
        Assert.Equal("", dialogue.Text);

        // 読みを直して続けて置く。同じレイヤーの続きに置かれる
        dialogue.Text = "していってね";
        dialogue.Reading = "シテイッテネ！！";
        await dialogue.AddCommand.ExecuteAsync(null);
        var second = (VoiceItem)session.Scene.GetItem(session.Selection.Single());
        Assert.Equal((first.End, first.Layer, "シテイッテネ！！", 24L), (second.Start, second.Layer, second.Reading, second.Length));

        // 置くのと声で 1 回の操作
        Assert.Equal("セリフの追加", session.Document.UndoDescription);
        session.Document.Undo();
        Assert.Single(session.Scene.Items);
    });

    [Fact]
    public Task ListensAndResynthesizes() => Ui.Run(async () =>
    {
        var (session, dialogue, player) = CreateDialogue();
        using var _ = session;
        AddCharacter(session, "魔理沙");

        dialogue.Text = "だぜ";
        await dialogue.ListenCommand.ExecuteAsync(null);
        Assert.NotNull(player.LastPlayed);
        Assert.Equal("読み：だぜ", dialogue.Status);
        Assert.Empty(session.Scene.Items);

        await dialogue.AddCommand.ExecuteAsync(null);
        var id = session.Selection.Single();
        var item = (VoiceItem)session.Scene.GetItem(id);
        session.Execute(EditCommands.UpdateItem(session.Project, session.SceneId, item with { Text = "なんだぜ" }));
        Assert.True(dialogue.ResynthesizeCommand.CanExecute(null));
        await dialogue.ResynthesizeCommand.ExecuteAsync(null);
        Assert.Equal(12, session.Scene.GetItem(id).Length);
    });

    [Fact]
    public Task PastesScripts() => Ui.Run(async () =>
    {
        var (session, dialogue, _) = CreateDialogue();
        using var _ = session;
        var reimu = AddCharacter(session, "霊夢");
        var marisa = AddCharacter(session, "魔理沙");
        dialogue.Character = reimu;

        _dialogs.Prompts.Enqueue("こんにちは\n魔理沙：よう\n\n霊夢「またね」");
        await dialogue.PasteScriptCommand.ExecuteAsync(null);

        var lines = session.Scene.Items.Values.OfType<VoiceItem>().OrderBy(item => item.Start).ToList();
        Assert.Equal([(reimu.Id, "こんにちは"), (marisa.Id, "よう"), (reimu.Id, "またね")], lines.Select(line => (line.Character, line.Text)));
        Assert.Equal([0L, 15, 21], lines.Select(line => line.Start));
        Assert.Equal(3, session.Selection.Count);
        Assert.Equal("台本の貼り付け", session.Document.UndoDescription);
    });

    [Fact]
    public Task RecordsToTheTimeline() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var input = new FakeAudioInput();
        var recording = new RecordingViewModel(session, _dialogs, new PreviewViewModel(session), _ => input);
        using var __ = recording;
        session.CurrentFrame = 30;

        await recording.ToggleRecordCommand.ExecuteAsync(null);
        Assert.True(recording.IsRecording);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(recording.Level.Left.PeakDb > -20, $"レベル {recording.Level.Left.PeakDb}");
        await recording.ToggleRecordCommand.ExecuteAsync(null);

        Assert.False(recording.IsRecording);
        Assert.True(input.Disposed);
        var item = Assert.IsType<AudioItem>(session.Scene.GetItem(session.Selection.Single()));
        Assert.Equal(30, item.Start);
        Assert.InRange(item.Length, 15, 30);
        var asset = session.Project.Assets[item.Asset];
        Assert.StartsWith("recordings/", asset.Path);
        Assert.True(File.Exists(Path.Combine(session.ProjectFolder, asset.Path)));
        Assert.Equal("録音", session.Document.UndoDescription);
    });

    [Fact]
    public Task RecordsLinesOneAfterAnother() => Ui.Run(async () =>
    {
        using var session = _environment.CreateSession();
        var character = AddCharacter(session, "霊夢");
        var layer = session.Scene.Layers[0].Id;
        var first = new VoiceItem(ItemId.New(), layer, 0, 30, character.Id, "一");
        var second = new VoiceItem(ItemId.New(), layer, 60, 30, character.Id, "二");
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, first));
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, second));
        var recording = new RecordingViewModel(session, _dialogs, new PreviewViewModel(session), _ => new FakeAudioInput());
        using var __ = recording;

        Assert.False(recording.RecordLineCommand.CanExecute(null));
        session.Selection = [first.Id];
        Assert.Equal("「一」", recording.LineLabel);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await recording.RecordLineCommand.ExecuteAsync(null);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await recording.RecordLineCommand.ExecuteAsync(null);
        var elapsedFrames = watch.Elapsed.TotalSeconds * 30;

        var recorded = (VoiceItem)session.Scene.GetItem(first.Id);
        Assert.IsType<RecordedVoice>(recorded.Source);

        // 録った長さは、実際にたった時間（混んだマシンでは Delay が長めになる）以内で、短すぎない
        Assert.InRange(recorded.Length, 6, Math.Max(15, (long)Math.Ceiling(elapsedFrames) + 1));
        Assert.Equal([second.Id], session.Selection);
        Assert.Equal(60, session.CurrentFrame);

        // 録り直し
        Assert.True(recording.RetakeCommand.CanExecute(null));
        await recording.RetakeCommand.ExecuteAsync(null);
        Assert.Equal([first.Id], session.Selection);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await recording.RecordLineCommand.ExecuteAsync(null);
        Assert.NotEqual(((RecordedVoice)recorded.Source).Asset, ((RecordedVoice)((VoiceItem)session.Scene.GetItem(first.Id)).Source).Asset);
    });
}
