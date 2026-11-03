using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>素材の一覧：追加・サムネイル・タイムラインへの配置・リンク切れ・外す</summary>
public sealed class MediaPoolTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public Task ImportsFilesOnceWithThumbnails() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = _environment.CreateSession();
        var pool = new MediaPoolViewModel(session, _dialogs);
        Assert.True(pool.IsEmpty);

        var broken = Path.Combine(_environment.Folder, "broken.mp4");
        await File.WriteAllTextAsync(broken, "壊れたファイル", TestContext.Current.CancellationToken);
        var assets = await pool.ImportAsync([Fixtures.Path("green-64x48.png"), Fixtures.Path("tone-1s.wav"), Fixtures.Path("frames-2997.mp4"), broken]);
        await pool.PreviewsReady;

        Assert.Equal([AssetKind.Image, AssetKind.Audio, AssetKind.Video], assets.Select(asset => asset.Kind));
        Assert.Contains("broken.mp4", Assert.Single(_dialogs.Errors));
        Assert.Equal(["frames-2997.mp4", "green-64x48.png", "tone-1s.wav"], pool.Assets.Select(entry => entry.Name));
        var image = pool.Assets.Single(entry => entry.Name == "green-64x48.png");
        Assert.Equal("64×48", image.Details);
        Assert.True(image.HasThumbnail);
        Assert.Equal("48kHz・0:01.0", pool.Assets.Single(entry => entry.IsAudio).Details);
        Assert.StartsWith("128×96・", pool.Assets.Single(entry => entry.KindLabel == "動画").Details);

        // 同じファイルは 2 回登録しない。元に戻すと消える
        Assert.Equal(assets[0].Id, (await pool.ImportAsync([Fixtures.Path("green-64x48.png")]))[0].Id);
        Assert.Equal(3, session.Project.Assets.Count);
        session.Document.Undo();
        Assert.True(pool.IsEmpty);
    });

    [Fact]
    public Task PlacesAssetsAtThePlayheadOnAFreeLayer() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = _environment.CreateSession();
        var pool = new MediaPoolViewModel(session, _dialogs);
        await pool.ImportAsync([Fixtures.Path("green-64x48.png"), Fixtures.Path("tone-1s.wav")]);
        session.CurrentFrame = 15;

        pool.Selected = pool.Assets.Single(entry => entry.Name == "green-64x48.png");
        await pool.AddToTimelineCommand.ExecuteAsync(null);
        await pool.AddToTimelineCommand.ExecuteAsync(null);
        pool.Selected = pool.Assets.Single(entry => entry.IsAudio);
        await pool.AddToTimelineCommand.ExecuteAsync(null);

        var scene = session.Scene;
        var images = scene.Items.Values.OfType<ImageItem>().OrderBy(item => scene.LayerIndex(item.Layer)).ToList();
        Assert.Equal([(15L, 150L, 0), (15L, 150L, 1)], images.Select(item => (item.Start, item.Length, scene.LayerIndex(item.Layer))));
        var audio = scene.Items.Values.OfType<AudioItem>().Single();
        Assert.Equal((15L, 30L, 2), (audio.Start, audio.Length, scene.LayerIndex(audio.Layer)));
        Assert.Equal([audio.Id], session.Selection);
        Assert.Equal(1, pool.Assets.Single(entry => entry.IsAudio).UseCount);

        // 使っている素材は外せない。使っていなければ外せる
        await pool.RemoveCommand.ExecuteAsync(null);
        Assert.Single(_dialogs.Errors);
        session.Execute(EditCommands.DeleteItems(session.Project, session.SceneId, [audio.Id]));
        pool.Selected = pool.Assets.Single(entry => entry.IsAudio);
        await pool.RemoveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(pool.Assets, entry => entry.IsAudio);
    });

    [Fact]
    public Task AddsLayersWhenEveryLayerIsTaken() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = _environment.CreateSession();
        var pool = new MediaPoolViewModel(session, _dialogs);
        await pool.ImportAsync([Fixtures.Path("tone-1s.wav")]);
        pool.Selected = pool.Assets[0];
        var layers = session.Scene.Layers.Count;

        for (var i = 0; i <= layers; i++)
        {
            await pool.AddToTimelineCommand.ExecuteAsync(null);
        }

        Assert.Equal(layers + 1, session.Scene.Layers.Count);
        Assert.Equal(layers + 1, session.Scene.Items.Count);
        session.Document.Undo(); // レイヤーの追加ごと元に戻る
        Assert.Equal(layers, session.Scene.Layers.Count);
    });

    [Fact]
    public Task RelinksMissingFiles() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = _environment.CreateSession();
        var pool = new MediaPoolViewModel(session, _dialogs);
        var original = Fixtures.CopyTo(Path.Combine(_environment.Folder, "a"), "green-64x48.png");
        var other = Fixtures.CopyTo(Path.Combine(_environment.Folder, "b"), "tone-1s.wav");
        await pool.ImportAsync([original, other]);
        await pool.PreviewsReady; // サムネイルを作り終えてからファイルを動かす

        Directory.CreateDirectory(Path.Combine(_environment.Folder, "moved"));
        Directory.Move(Path.Combine(_environment.Folder, "a"), Path.Combine(_environment.Folder, "moved", "a"));
        File.Move(other, Path.Combine(_environment.Folder, "renamed.wav"));
        session.Execute(new SetAsset(session.Project.Assets.Values.First().Id, session.Project.Assets.Values.First())); // 一覧を作り直す
        Assert.Equal(2, pool.MissingCount);
        Assert.True(pool.HasMissing);

        // フォルダから同じ名前で探す
        _dialogs.Folders.Enqueue(Path.Combine(_environment.Folder, "moved"));
        await pool.SearchMissingCommand.ExecuteAsync(null);
        Assert.Equal(1, pool.MissingCount);
        Assert.Contains("1 個の素材を見つけました", Assert.Single(_dialogs.Messages));

        // 名前が変わったものは、1 つずつ指定する
        pool.Selected = pool.Assets.Single(entry => entry.IsMissing);
        _dialogs.FilesToOpen.Enqueue(Path.Combine(_environment.Folder, "renamed.wav"));
        await pool.RelinkCommand.ExecuteAsync(null);
        Assert.Equal(0, pool.MissingCount);
        Assert.Contains(pool.Assets, entry => entry.Name == "renamed.wav");
    });
}
