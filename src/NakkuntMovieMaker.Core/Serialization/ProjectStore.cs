using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Serialization;

/// <summary>
/// プロジェクトをフォルダ形式で保存・読み込みする。
/// <code>
/// MyVideo/
///   MyVideo.nmmproj                    入口（形式のバージョン・名前・シーンの一覧）
///   scenes/&lt;id&gt;.scene.json             シーンごとに 1 ファイル
///   characters/&lt;id&gt;.character.json     キャラクターごとに 1 ファイル
///   assets.json                        素材の一覧
///   voices/  recordings/               生成した声・録音した声
///   .nmm/                              キャッシュと自動保存（Git では除外）
/// </code>
/// ファイルは一時ファイルに書いてから名前を変えるので、保存中に落ちても壊れたファイルが残らない
/// </summary>
public sealed class ProjectStore
{
    public const string EntryExtension = ".nmmproj";
    public const string ScenesDirectory = "scenes";
    public const string CharactersDirectory = "characters";
    public const string AssetsFile = "assets.json";
    public const string SceneExtension = ".scene.json";
    public const string CharacterExtension = ".character.json";

    private readonly IReadOnlyList<IProjectMigration> _migrations;
    private readonly int _currentVersion;

    public ProjectStore()
        : this(ProjectMigrations.All, Project.CurrentSchemaVersion)
    {
    }

    /// <summary>移行処理と今の形式のバージョンを指定する（テスト用）</summary>
    public ProjectStore(IReadOnlyList<IProjectMigration> migrations, int currentVersion)
    {
        _migrations = migrations;
        _currentVersion = currentVersion;
    }

    /// <summary>プロジェクトを保存する。entryPath は入口のファイル（.nmmproj）のパス</summary>
    public void Save(Project project, string entryPath)
    {
        ArgumentNullException.ThrowIfNull(project);

        var folder = GetFolder(entryPath);
        var scenes = Path.Combine(folder, ScenesDirectory);
        var characters = Path.Combine(folder, CharactersDirectory);
        Directory.CreateDirectory(scenes);
        Directory.CreateDirectory(characters);

        var sceneFiles = project.Scenes.Values.ToDictionary(scene => SceneFileName(scene.Id), ProjectJsonWriter.WriteScene);
        var characterFiles = project.Characters.Values.ToDictionary(character => CharacterFileName(character.Id), ProjectJsonWriter.WriteCharacter);

        foreach (var (name, bytes) in sceneFiles)
        {
            WriteIfChanged(Path.Combine(scenes, name), bytes);
        }

        foreach (var (name, bytes) in characterFiles)
        {
            WriteIfChanged(Path.Combine(characters, name), bytes);
        }

        DeleteStaleFiles(scenes, SceneExtension, sceneFiles.Keys);
        DeleteStaleFiles(characters, CharacterExtension, characterFiles.Keys);
        WriteIfChanged(Path.Combine(folder, AssetsFile), ProjectJsonWriter.WriteAssets(project.Assets.Values));

        var gitignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(gitignore))
        {
            WriteAtomic(gitignore, Encoding.UTF8.GetBytes("# NakkuntMovieMaker のキャッシュと自動保存\n.nmm/\n"));
        }

        // 入口は最後に書く。途中で失敗しても、前の入口から読める状態を保つ
        WriteIfChanged(Path.GetFullPath(entryPath), ProjectJsonWriter.WriteEntry(project));
    }

    /// <summary>プロジェクトを読み込む。古い形式なら移行処理を順に適用する</summary>
    /// <exception cref="ProjectFormatException">ファイルが読めない・形式が正しくない・新しすぎる形式</exception>
    public Project Load(string entryPath)
    {
        var folder = GetFolder(entryPath);
        var entryName = Path.GetFileName(entryPath);

        var entryBytes = ReadFile(folder, entryName);
        var version = Parse(entryName, entryBytes, root => new ProjectJsonReader(entryName).ReadSchemaVersion(root));
        if (version > _currentVersion)
        {
            throw new ProjectFormatException(entryName,
                $"このプロジェクトは新しいバージョンのソフトで保存されています（形式 {version}、このソフトは {_currentVersion} まで）");
        }

        var files = new Dictionary<string, byte[]> { [entryName] = entryBytes };
        foreach (var path in EnumerateFiles(folder, ScenesDirectory, SceneExtension).Concat(EnumerateFiles(folder, CharactersDirectory, CharacterExtension)))
        {
            files[path] = ReadFile(folder, path);
        }

        var assetsBytes = File.Exists(Path.Combine(folder, AssetsFile)) ? ReadFile(folder, AssetsFile) : "{\"assets\":[]}"u8.ToArray();
        files[AssetsFile] = assetsBytes;

        if (version < _currentVersion)
        {
            files = Migrate(files, entryName, version);
        }

        var entry = Parse(entryName, files[entryName], root => new ProjectJsonReader(entryName).ReadEntry(root));

        var scenes = ImmutableDictionary.CreateBuilder<SceneId, Scene>();
        foreach (var id in entry.Scenes)
        {
            var path = $"{ScenesDirectory}/{SceneFileName(id)}";
            if (!files.TryGetValue(path, out var bytes))
            {
                throw new ProjectFormatException(path, "シーンのファイルが見つかりません");
            }

            var scene = Parse(path, bytes, root => new ProjectJsonReader(path).ReadScene(root));
            if (scene.Id != id)
            {
                throw new ProjectFormatException(path, "ファイル名とシーンの ID が一致しません");
            }

            scenes[id] = scene;
        }

        if (!scenes.ContainsKey(entry.RootScene))
        {
            throw new ProjectFormatException(entryName, "最初に開くシーンが、シーンの一覧にありません");
        }

        var characters = ImmutableDictionary.CreateBuilder<CharacterId, Character>();
        foreach (var (path, bytes) in files.Where(pair => pair.Key.StartsWith(CharactersDirectory + "/", StringComparison.Ordinal)))
        {
            var character = Parse(path, bytes, root => new ProjectJsonReader(path).ReadCharacter(root));
            characters[character.Id] = character;
        }

        var assets = Parse(AssetsFile, files[AssetsFile], root => new ProjectJsonReader(AssetsFile).ReadAssets(root));

        return new Project(_currentVersion, entry.Name, entry.RootScene, scenes.ToImmutable(), characters.ToImmutable(), assets)
        {
            Color = entry.Color,
            Proxies = entry.Proxies,
        };
    }

    public static string SceneFileName(SceneId id) => id + SceneExtension;

    public static string CharacterFileName(CharacterId id) => id + CharacterExtension;

    private Dictionary<string, byte[]> Migrate(Dictionary<string, byte[]> files, string entryName, int version)
    {
        JsonObject ParseObject(string path) =>
            JsonNode.Parse(files[path]) as JsonObject ?? throw new ProjectFormatException(path, "JSON のオブジェクトではありません");

        var json = new ProjectJsonFiles
        {
            Entry = ParseObject(entryName),
            Scenes = files.Keys.Where(path => path.StartsWith(ScenesDirectory + "/", StringComparison.Ordinal)).ToDictionary(path => path, ParseObject),
            Characters = files.Keys.Where(path => path.StartsWith(CharactersDirectory + "/", StringComparison.Ordinal)).ToDictionary(path => path, ParseObject),
            Assets = ParseObject(AssetsFile),
        };

        for (; version < _currentVersion; version++)
        {
            var migration = _migrations.SingleOrDefault(candidate => candidate.FromVersion == version)
                ?? throw new ProjectFormatException(entryName, $"形式 {version} から移行する処理がありません");
            migration.Apply(json);
            json.Entry["schemaVersion"] = version + 1;
        }

        var migrated = new Dictionary<string, byte[]>
        {
            [entryName] = JsonSerializerToBytes(json.Entry),
            [AssetsFile] = JsonSerializerToBytes(json.Assets),
        };
        foreach (var (path, node) in json.Scenes.Concat(json.Characters))
        {
            migrated[path] = JsonSerializerToBytes(node);
        }

        return migrated;
    }

    private static byte[] JsonSerializerToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static T Parse<T>(string path, byte[] bytes, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return read(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(path, $"JSON として読めません（{ex.LineNumber + 1} 行目）", ex);
        }
    }

    private static string GetFolder(string entryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        return Path.GetDirectoryName(Path.GetFullPath(entryPath))
            ?? throw new ArgumentException("プロジェクトのフォルダが分かりません", nameof(entryPath));
    }

    private static byte[] ReadFile(string folder, string relativePath)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(folder, relativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFormatException(relativePath, $"ファイルを読めません（{ex.Message}）", ex);
        }
    }

    // フォルダ内の指定した拡張子のファイルを、"フォルダ名/ファイル名" の形で返す
    private static IEnumerable<string> EnumerateFiles(string folder, string directory, string extension)
    {
        var path = Path.Combine(folder, directory);
        if (!Directory.Exists(path))
        {
            return [];
        }

        return Directory.EnumerateFiles(path, "*" + extension)
            .Select(file => $"{directory}/{Path.GetFileName(file)}")
            .Order(StringComparer.Ordinal);
    }

    private static void DeleteStaleFiles(string directory, string extension, IEnumerable<string> keep)
    {
        var keepSet = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*" + extension))
        {
            if (!keepSet.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
    }

    // 中身が変わっていなければ書かない（Git や同期ソフトから見て無駄な更新にならないように）
    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        WriteAtomic(path, bytes);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
