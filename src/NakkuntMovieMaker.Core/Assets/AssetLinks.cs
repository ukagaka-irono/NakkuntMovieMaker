using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Assets;

/// <summary>
/// 素材ファイルの場所の扱い。プロジェクトのフォルダ内の素材は相対パスで持つので、
/// フォルダごと移動・共有してもリンクが切れない。パスの区切りは OS によらず "/" で保存する
/// </summary>
public static class AssetLinks
{
    /// <summary>素材ファイルの絶対パス</summary>
    public static string Resolve(Asset asset, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var path = asset.Path.Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(projectFolder, path));
    }

    /// <summary>保存用のパス。プロジェクトのフォルダの中なら相対パス、外なら絶対パスにする</summary>
    public static string ToStoredPath(string fullPath, string projectFolder)
    {
        var full = Path.GetFullPath(fullPath);
        var folder = Path.GetFullPath(projectFolder);
        var relative = Path.GetRelativePath(folder, full);
        var inside = !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return (inside ? relative : full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>ファイルが見つからない素材（リンク切れ）を ID の順に返す</summary>
    public static IReadOnlyList<Asset> FindMissing(Project project, string projectFolder) =>
        [.. project.Assets.Values
            .Where(asset => !File.Exists(Resolve(asset, projectFolder)))
            .OrderBy(asset => asset.Id.ToString(), StringComparer.Ordinal)];

    /// <summary>素材のファイルを指定し直す操作</summary>
    public static IOperation Relink(Project project, AssetId asset, string newFullPath, string projectFolder)
    {
        var current = project.Assets.TryGetValue(asset, out var found)
            ? found
            : throw new EditException("素材が見つかりません");
        return new Transaction("素材の再指定", [new SetAsset(asset, current with { Path = ToStoredPath(newFullPath, projectFolder) })]);
    }

    /// <summary>
    /// リンク切れの素材と同じ名前のファイルを、searchFolder の中から探す（サブフォルダも含む）。
    /// 見つかった候補を、素材の ID → ファイルの絶対パスで返す。同じ名前が複数あれば最初に見つかったものを使う
    /// </summary>
    public static IReadOnlyDictionary<AssetId, string> FindByFileName(Project project, string projectFolder, string searchFolder)
    {
        var missing = FindMissing(project, projectFolder);
        if (missing.Count == 0 || !Directory.Exists(searchFolder))
        {
            return new Dictionary<AssetId, string>();
        }

        var names = missing
            .GroupBy(asset => Path.GetFileName(asset.Path.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<AssetId, string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(searchFolder, "*", options).Order(StringComparer.Ordinal))
        {
            if (names.TryGetValue(Path.GetFileName(file), out var assets))
            {
                foreach (var asset in assets)
                {
                    found.TryAdd(asset.Id, Path.GetFullPath(file));
                }
            }
        }

        return found;
    }
}
