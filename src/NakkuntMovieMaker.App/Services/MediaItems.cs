using Avalonia.Input;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.Services;

/// <summary>素材の追加と、素材からタイムラインのアイテムを作る処理（素材の一覧とタイムラインの両方から使う）</summary>
public static class MediaItems
{
    /// <summary>素材の一覧からタイムラインへドラッグするときのデータ（素材の ID）</summary>
    public static readonly DataFormat<string> AssetFormat = DataFormat.CreateInProcessFormat<string>("NakkuntMovieMaker.Asset");

    /// <summary>画像を置くときの長さ（秒）</summary>
    public const int ImageSeconds = 5;

    /// <summary>
    /// ファイルを素材として登録する操作と、その素材（すでに登録してあるファイルは、登録済みの素材を返す）。
    /// 開けないファイルは failures に理由を入れる
    /// </summary>
    public static (IOperation? Operation, IReadOnlyList<Asset> Assets) Import(
        Project project, string projectFolder, IEnumerable<string> paths, List<string> failures)
    {
        var operations = new List<IOperation>();
        var assets = new List<Asset>();
        var known = project.Assets.Values.ToDictionary(asset => AssetLinks.Resolve(asset, projectFolder), StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (known.TryGetValue(path, out var existing))
            {
                assets.Add(existing);
                continue;
            }

            MediaInfo info;
            try
            {
                info = MediaProbe.Probe(path);
            }
            catch (Exception ex) when (ex is MediaException or InvalidOperationException or IOException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            var asset = new Asset(AssetId.New(), info.Kind, AssetLinks.ToStoredPath(path, projectFolder));
            operations.Add(new SetAsset(asset.Id, asset));
            assets.Add(asset);
            known[path] = asset;
        }

        return (operations.Count == 0 ? null : Operations.Combine("素材の追加", operations), assets);
    }

    /// <summary>素材のアイテム。長さは動画・音声なら素材の長さ、画像なら 5 秒</summary>
    public static Item CreateItem(Asset asset, string projectFolder, LayerId layer, long start, Rational fps)
    {
        long length;
        try
        {
            var info = asset.Kind == AssetKind.Image ? null : MediaProbe.Probe(AssetLinks.Resolve(asset, projectFolder));
            length = info?.Duration is { } duration
                ? Math.Max(1, TimeMath.SecondsToFrame(duration, fps, Rounding.Up))
                : TimeMath.SecondsToFrame(ImageSeconds, fps);
        }
        catch (Exception ex) when (ex is MediaException or InvalidOperationException or IOException)
        {
            length = TimeMath.SecondsToFrame(ImageSeconds, fps);
        }

        return asset.Kind switch
        {
            AssetKind.Video => new VideoItem(ItemId.New(), layer, start, length, asset.Id),
            AssetKind.Image => new ImageItem(ItemId.New(), layer, start, length, asset.Id),
            _ => new AudioItem(ItemId.New(), layer, start, length, asset.Id),
        };
    }

    /// <summary>
    /// [start, start + length) が空いているレイヤー。preferred から上へ探し、なければ一番上の上に新しいレイヤーを作る
    /// （作るときは、その操作も返す）
    /// </summary>
    public static (LayerId Layer, IOperation? AddLayer) FindFreeLayer(Project project, SceneId sceneId, long start, long length, int preferred = 0)
    {
        var scene = project.GetScene(sceneId);
        for (var index = Math.Max(0, preferred); index < scene.Layers.Count; index++)
        {
            var layer = scene.Layers[index];
            if (!layer.Locked && !scene.ItemsOnLayer(layer.Id).Any(item => item.Start < start + length && start < item.End))
            {
                return (layer.Id, null);
            }
        }

        var add = EditCommands.AddLayer(project, sceneId, scene.Layers.Count, $"レイヤー {scene.Layers.Count + 1}");
        var added = add.Apply(project).Project.GetScene(sceneId).Layers[^1];
        return (added.Id, add);
    }

    /// <summary>素材を、指定した位置の空いているレイヤーに置く操作と、置いたアイテム</summary>
    public static (IOperation Operation, Item Item) Place(Project project, SceneId sceneId, string projectFolder, Asset asset, long start, int preferredLayer)
    {
        var fps = project.GetScene(sceneId).Fps;
        var probe = CreateItem(asset, projectFolder, default, start, fps);
        var (layer, addLayer) = FindFreeLayer(project, sceneId, start, probe.Length, preferredLayer);
        var item = probe with { Layer = layer };
        var operations = new List<IOperation>();
        if (addLayer is not null)
        {
            operations.Add(addLayer);
            project = addLayer.Apply(project).Project;
        }

        operations.Add(EditCommands.AddItem(project, sceneId, item));
        return (Operations.Combine("素材をタイムラインに置く", operations), item);
    }
}
