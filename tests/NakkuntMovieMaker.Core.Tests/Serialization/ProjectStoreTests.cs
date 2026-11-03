using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Core.Tests.Editing;

namespace NakkuntMovieMaker.Core.Tests.Serialization;

public class ProjectStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ProjectStore _store = new();

    public void Dispose() => _folder.Dispose();

    private string EntryPath => _folder.Combine("MyVideo.nmmproj");

    /// <summary>保存形式のすべての要素を含むプロジェクト</summary>
    private static Project RichProject()
    {
        var t = new TestProject();
        var project = t.Project;
        var scene = t.Scene(project);

        var effect = new EffectInstance(EffectId.New(), "nmm.blur", ImmutableDictionary<string, ParameterValue>.Empty
            .Add("radius", new NumberParameter(Animatable.FromKeys(
                new Keyframe<double>(0, 0, new Easing(EasingCurve.Cubic, EasingMode.Out)),
                new Keyframe<double>(30, 12.5))))
            .Add("tint", new ColorParameter(Animatable.Constant(new RgbaColor(255, 0, 0, 128))))
            .Add("glow", new ColorParameter(Animatable.FromKeys(new Keyframe<RgbaColor>(0, RgbaColor.Black), new Keyframe<RgbaColor>(10, RgbaColor.White, Easing.Hold))))
            .Add("enabled", new BoolParameter(true))
            .Add("mode", new TextParameter("gaussian")), Enabled: false);

        var text = new TextItem(ItemId.New(), t.Layers[3], 200, 30, "こんにちは\n「世界」")
        {
            Style = TextStyle.Default with
            {
                FontFamily = "Noto Sans JP",
                Shadow = new TextShadow(RgbaColor.Black, 2, 3, 1.5),
                Background = new TextBackground(new RgbaColor(0, 0, 0, 160), 12, 6),
                Alignment = TextAlignment.Left,
                Vertical = true,
                RevealCharsPerSecond = 15,
            },
            Transform = Transform2D.Identity with
            {
                X = Animatable.FromKeys(new Keyframe<double>(0, -100.25, new Easing(EasingCurve.Back, EasingMode.InOut)), new Keyframe<double>(29, 0.1)),
                Rotation = 12.5,
                FlipVertical = true,
            },
            Blend = BlendMode.Screen,
            Crop = new Crop(1, 2, 3, 4.5),
            Effects = [effect],
        };
        var gradient = new ShapeItem(ItemId.New(), t.Layers[3], 300, 10,
            new ShapeStyle(ShapeKind.Star, 200, 180, new LinearGradientPaint(RgbaColor.White, RgbaColor.Black, 45), RgbaColor.Black, 2, 0, 7, 0.4));
        var recorded = new AudioItem(ItemId.New(), t.Layers[3], 400, 50, AssetId.New())
        {
            Playback = new MediaPlayback(new(1001, 30000), new(3, 2), Reverse: true),
            Audio = new AudioMix(Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(10, 1)), -0.5, 5, 10, PreservePitch: false),
        };
        var voice = t.Voice with
        {
            Reading = "ユックリ'シテイッテネ",
            Source = new RecordedVoice(AssetId.New()),
            VoiceOverride = new VoiceParams(1.2, 0.9, 1.1, 0.8),
            SubtitleOverride = TextStyle.Default with { Color = new RgbaColor(255, 200, 0) },
        };
        var tachie = new TachieItem(ItemId.New(), t.Layers[2], 100, 50, t.Voice.Character);
        var marker = new Marker(MarkerId.New(), 15, "サビ", new RgbaColor(255, 128, 0));

        scene = scene with
        {
            Items = scene.Items.SetItem(text.Id, text).SetItem(gradient.Id, gradient).SetItem(recorded.Id, recorded)
                .SetItem(voice.Id, voice).SetItem(tachie.Id, tachie),
            Markers = [marker],
        };

        var character = project.Characters[t.Voice.Character] with { Tachie = new TachieSpec(TachieKind.Parts, "立ち絵/霊夢") };
        var second = Scene.CreateNew("サブ", SceneFormat.Vertical1080x1920p30 with { Fps = new(30000, 1001) }, 2);
        return project.WithScene(scene).WithScene(second) with
        {
            Characters = project.Characters.SetItem(character.Id, character),
            Assets = project.Assets.Add(recorded.Asset, new Asset(recorded.Asset, AssetKind.Audio, "recordings/2026-11-03.wav", "abc123")),
        };
    }

    [Fact]
    public void SavedProjectLoadsBackUnchanged()
    {
        var project = RichProject();

        _store.Save(project, EntryPath);
        var loaded = _store.Load(EntryPath);

        Assert.Equal(project, loaded);
    }

    [Fact]
    public void SavingTheLoadedProjectProducesIdenticalBytes()
    {
        _store.Save(RichProject(), EntryPath);
        var loaded = _store.Load(EntryPath);

        using var other = new TempFolder();
        var otherEntry = other.Combine("MyVideo.nmmproj");
        _store.Save(loaded, otherEntry);

        var files = Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_folder.Path, path))
            .Order(StringComparer.Ordinal)
            .ToList();
        var otherFiles = Directory.EnumerateFiles(other.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(other.Path, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(files, otherFiles);
        foreach (var file in files)
        {
            Assert.Equal(File.ReadAllBytes(_folder.Combine(file)), File.ReadAllBytes(other.Combine(file)));
        }
    }

    [Fact]
    public void FilesUseUtf8LfTwoSpaceIndentAndReadableText()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);

        var sceneFile = _folder.Combine(ProjectStore.ScenesDirectory, ProjectStore.SceneFileName(project.RootScene));
        var bytes = File.ReadAllBytes(sceneFile);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)); // BOM なし
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\n  \"name\": \"メイン\",", text);
        Assert.Contains("こんにちは\\n「世界」", text); // 日本語はエスケープしない
        Assert.Contains("\"opacity\": 1,", text); // アニメーションしない値は数値だけ
        Assert.Contains("\"easing\": \"back-in-out\"", text);
        Assert.Contains("\"fps\": \"30\"", text);
    }

    [Fact]
    public void ItemsAreWrittenInIdOrder()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);

        var scene = JsonNode.Parse(File.ReadAllText(_folder.Combine(ProjectStore.ScenesDirectory, ProjectStore.SceneFileName(project.RootScene))))!;
        var ids = scene["items"]!.AsArray().Select(item => (string)item!["id"]!).ToList();

        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public void UnchangedFilesAreNotRewritten()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);
        var assets = _folder.Combine(ProjectStore.AssetsFile);
        var written = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(assets, written);

        _store.Save(project, EntryPath);

        Assert.Equal(written, File.GetLastWriteTimeUtc(assets));
        Assert.Empty(Directory.EnumerateFiles(_folder.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void RemovedScenesAndCharactersAreDeleted()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);
        var removedScene = project.Scenes.Keys.Single(id => id != project.RootScene);

        _store.Save(project with { Scenes = project.Scenes.Remove(removedScene), Characters = project.Characters.Clear() }, EntryPath);

        Assert.Single(Directory.EnumerateFiles(_folder.Combine(ProjectStore.ScenesDirectory)));
        Assert.Empty(Directory.EnumerateFiles(_folder.Combine(ProjectStore.CharactersDirectory)));
        Assert.Single(_store.Load(EntryPath).Scenes);
    }

    [Fact]
    public void CreatesGitignoreForCacheOnce()
    {
        _store.Save(RichProject(), EntryPath);
        var gitignore = _folder.Combine(".gitignore");

        Assert.Contains(".nmm/", File.ReadAllText(gitignore));

        File.WriteAllText(gitignore, "custom\n");
        _store.Save(RichProject(), EntryPath);
        Assert.Equal("custom\n", File.ReadAllText(gitignore));
    }

    [Fact]
    public void UnknownItemTypesAndParametersSurviveEditsAndSaves()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);
        var scenePath = _folder.Combine(ProjectStore.ScenesDirectory, ProjectStore.SceneFileName(project.RootScene));
        var scene = JsonNode.Parse(File.ReadAllText(scenePath))!;
        var layer = (string)scene["layers"]![3]!["id"]!;
        var pluginItemId = ItemId.New();
        scene["items"]!.AsArray().Add(new JsonObject
        {
            ["$type"] = "plugin.particles",
            ["id"] = pluginItemId.ToString(),
            ["layer"] = layer,
            ["start"] = 500,
            ["length"] = 20,
            ["effects"] = new JsonArray(new JsonObject
            {
                ["id"] = EffectId.New().ToString(),
                ["type"] = "plugin.glow",
                ["enabled"] = true,
                ["parameters"] = new JsonObject { ["curve"] = new JsonObject { ["$type"] = "bezier", ["points"] = new JsonArray(0, 0.5, 1) } },
            }),
            ["count"] = 1000,
            ["settings"] = new JsonObject { ["gravity"] = 9.8 },
        });
        File.WriteAllText(scenePath, scene.ToJsonString());

        var loaded = _store.Load(EntryPath);
        var item = Assert.IsType<UnknownItem>(loaded.Root.GetItem(pluginItemId));
        Assert.IsType<UnknownParameter>(item.Effects[0].Parameters["curve"]);

        var moved = EditCommands.MoveItems(loaded, loaded.RootScene, [pluginItemId], 10).Apply(loaded).Project;
        _store.Save(moved, EntryPath);

        var saved = JsonNode.Parse(File.ReadAllText(scenePath))!["items"]!.AsArray()
            .Single(node => (string)node!["id"]! == pluginItemId.ToString())!;
        Assert.Equal(510, (long)saved["start"]!);
        Assert.Equal(1000, (int)saved["count"]!);
        Assert.Equal(9.8, (double)saved["settings"]!["gravity"]!);
        Assert.Equal("bezier", (string)saved["effects"]![0]!["parameters"]!["curve"]!["$type"]!);
        Assert.Equal(moved, _store.Load(EntryPath));
    }

    [Fact]
    public void ReportsTheFileAndFieldOfFormatErrors()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);
        var sceneName = $"{ProjectStore.ScenesDirectory}/{ProjectStore.SceneFileName(project.RootScene)}";
        var scenePath = _folder.Combine(ProjectStore.ScenesDirectory, ProjectStore.SceneFileName(project.RootScene));
        var scene = JsonNode.Parse(File.ReadAllText(scenePath))!;
        scene["format"]!.AsObject().Remove("width");
        File.WriteAllText(scenePath, scene.ToJsonString());

        var error = Assert.Throws<ProjectFormatException>(() => _store.Load(EntryPath));

        Assert.Equal(sceneName, error.File);
        Assert.Contains("width", error.Message);
    }

    [Fact]
    public void BrokenJsonIsReported()
    {
        _store.Save(RichProject(), EntryPath);
        File.WriteAllText(_folder.Combine(ProjectStore.AssetsFile), "{ \"assets\": [ ");

        var error = Assert.Throws<ProjectFormatException>(() => _store.Load(EntryPath));
        Assert.Equal(ProjectStore.AssetsFile, error.File);
    }

    [Fact]
    public void ProjectsFromNewerVersionsAreRejected()
    {
        _store.Save(RichProject(), EntryPath);
        var entry = JsonNode.Parse(File.ReadAllText(EntryPath))!;
        entry["schemaVersion"] = Project.CurrentSchemaVersion + 1;
        File.WriteAllText(EntryPath, entry.ToJsonString());

        var error = Assert.Throws<ProjectFormatException>(() => _store.Load(EntryPath));
        Assert.Contains("新しいバージョン", error.Message);
    }

    [Fact]
    public void OlderProjectsAreMigrated()
    {
        var project = RichProject();
        _store.Save(project, EntryPath);
        // 形式 1 を「name を title と呼んでいた形式 0」に見せかける
        var entry = JsonNode.Parse(File.ReadAllText(EntryPath))!.AsObject();
        entry["schemaVersion"] = 0;
        entry["title"] = entry["name"]!.DeepClone();
        entry.Remove("name");
        File.WriteAllText(EntryPath, entry.ToJsonString());

        var migrating = new ProjectStore([new RenameTitleMigration()], Project.CurrentSchemaVersion);
        var loaded = migrating.Load(EntryPath);

        Assert.Equal(project, loaded);
        Assert.Throws<ProjectFormatException>(() => _store.Load(EntryPath)); // 移行処理がなければ読めない
    }

    private sealed class RenameTitleMigration : IProjectMigration
    {
        public int FromVersion => 0;

        public void Apply(ProjectJsonFiles files)
        {
            files.Entry["name"] = files.Entry["title"]!.DeepClone();
            files.Entry.Remove("title");
        }
    }
}
