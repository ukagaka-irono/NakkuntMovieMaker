using System.Text.Json.Nodes;

namespace NakkuntMovieMaker.Core.Serialization;

/// <summary>
/// 古い形式で保存されたプロジェクトの JSON。移行処理はこれを書き換えて、1 つ新しい形式にする。
/// キーはプロジェクトのフォルダからの相対パス（"scenes/xxx.scene.json" など）
/// </summary>
public sealed class ProjectJsonFiles
{
    public required JsonObject Entry { get; init; }

    public required Dictionary<string, JsonObject> Scenes { get; init; }

    public required Dictionary<string, JsonObject> Characters { get; init; }

    public required JsonObject Assets { get; init; }
}

/// <summary>プロジェクトの形式を FromVersion から FromVersion + 1 に移す</summary>
public interface IProjectMigration
{
    int FromVersion { get; }

    void Apply(ProjectJsonFiles files);
}

/// <summary>組み込みの移行処理。形式を変えたら、ここに追加して Project.CurrentSchemaVersion を上げる</summary>
public static class ProjectMigrations
{
    public static IReadOnlyList<IProjectMigration> All { get; } = [new AddColorSettings()];

    // 1 → 2：色の設定・プロキシの設定・素材の入力の色空間を加えた。どれも、ないときは既定値（M1 と同じ処理）なので、書き換えるものはない
    private sealed class AddColorSettings : IProjectMigration
    {
        public int FromVersion => 1;

        public void Apply(ProjectJsonFiles files)
        {
        }
    }
}
