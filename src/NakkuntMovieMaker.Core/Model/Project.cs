using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Model;

/// <summary>
/// プロジェクト全体のスナップショット。不変で、編集するたびに新しいスナップショットを作る。
/// 変わっていない部分は前のスナップショットと同じインスタンスを共有する
/// </summary>
public sealed record Project(
    int SchemaVersion,
    string Name,
    SceneId RootScene,
    ImmutableDictionary<SceneId, Scene> Scenes,
    ImmutableDictionary<CharacterId, Character> Characters,
    ImmutableDictionary<AssetId, Asset> Assets)
{
    /// <summary>今のプロジェクト形式のバージョン。形式を変えたら上げ、移行処理を追加する</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>新しいプロジェクト。指定した形式のシーンを 1 つと、空のレイヤーを用意する</summary>
    public static Project CreateNew(string name, SceneFormat format, int layerCount = 10)
    {
        var scene = Scene.CreateNew("メイン", format, layerCount);
        return new Project(
            CurrentSchemaVersion,
            name,
            scene.Id,
            ImmutableDictionary<SceneId, Scene>.Empty.Add(scene.Id, scene),
            ImmutableDictionary<CharacterId, Character>.Empty,
            ImmutableDictionary<AssetId, Asset>.Empty);
    }

    public Scene Root => Scenes[RootScene];

    public Scene GetScene(SceneId id) =>
        Scenes.TryGetValue(id, out var scene) ? scene : throw new KeyNotFoundException($"シーンが見つかりません: {id}");

    public Project WithScene(Scene scene) => this with { Scenes = Scenes.SetItem(scene.Id, scene) };

    // コレクションは中身で比較する（ImmutableDictionary は参照で比較されるため）
    public bool Equals(Project? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && SchemaVersion == other.SchemaVersion
            && Name == other.Name
            && RootScene == other.RootScene
            && CollectionEquality.DictionaryEquals(Scenes, other.Scenes)
            && CollectionEquality.DictionaryEquals(Characters, other.Characters)
            && CollectionEquality.DictionaryEquals(Assets, other.Assets));

    public override int GetHashCode() => HashCode.Combine(SchemaVersion, Name, RootScene, Scenes.Count);
}

/// <summary>シーンの解像度・フレームレート・サンプルレート</summary>
public sealed record SceneFormat(int Width, int Height, Rational Fps, int SampleRate)
{
    /// <summary>横長 1920×1080・30fps</summary>
    public static readonly SceneFormat Landscape1080p30 = new(1920, 1080, 30, 48000);

    /// <summary>横長 1920×1080・60fps</summary>
    public static readonly SceneFormat Landscape1080p60 = new(1920, 1080, 60, 48000);

    /// <summary>縦長 1080×1920・30fps（ショート動画）</summary>
    public static readonly SceneFormat Vertical1080x1920p30 = new(1080, 1920, 30, 48000);

    /// <summary>正方形 1080×1080・30fps</summary>
    public static readonly SceneFormat Square1080p30 = new(1080, 1080, 30, 48000);
}

/// <summary>素材の種類</summary>
public enum AssetKind
{
    Video,
    Image,
    Audio,
}

/// <summary>
/// 素材ファイル。Path はプロジェクトのフォルダからの相対パスか、絶対パス。
/// Hash は内容のハッシュ（リンク切れのときに同じファイルを探すのに使う）
/// </summary>
public sealed record Asset(AssetId Id, AssetKind Kind, string Path, string? Hash = null);
