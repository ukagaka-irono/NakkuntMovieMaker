using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Tests.Assets;

public class AssetLinksTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string ProjectFolder => _folder.Combine("project");

    private Project ProjectWith(params Asset[] assets) =>
        Project.CreateNew("テスト", SceneFormat.Landscape1080p30) with
        {
            Assets = assets.ToImmutableDictionary(asset => asset.Id),
        };

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void StoresPathsInsideTheProjectAsRelative()
    {
        var inside = Path.Combine(ProjectFolder, "素材", "bg.png");
        var outside = _folder.Combine("other", "bgm.mp3");

        Assert.Equal("素材/bg.png", AssetLinks.ToStoredPath(inside, ProjectFolder));
        Assert.Equal(Path.GetFullPath(outside).Replace('\\', '/'), AssetLinks.ToStoredPath(outside, ProjectFolder));
        Assert.Equal(Path.GetFullPath(outside).Replace('\\', '/'),
            AssetLinks.ToStoredPath(outside, _folder.Combine("other-project")));
    }

    [Fact]
    public void ResolvesRelativeAndAbsolutePaths()
    {
        var relative = new Asset(AssetId.New(), AssetKind.Image, "素材/bg.png");
        var absolutePath = _folder.Combine("other", "bgm.mp3");
        var absolute = new Asset(AssetId.New(), AssetKind.Audio, absolutePath.Replace('\\', '/'));

        Assert.Equal(Path.Combine(ProjectFolder, "素材", "bg.png"), AssetLinks.Resolve(relative, ProjectFolder));
        Assert.Equal(Path.GetFullPath(absolutePath), AssetLinks.Resolve(absolute, ProjectFolder));
    }

    [Fact]
    public void FindsMissingAssetsAndRelinksThem()
    {
        CreateFile(Path.Combine(ProjectFolder, "ok.png"));
        var ok = new Asset(AssetId.New(), AssetKind.Image, "ok.png");
        var missing = new Asset(AssetId.New(), AssetKind.Video, "moved/clip.mp4", "hash");
        var project = ProjectWith(ok, missing);

        Assert.Equal([missing], AssetLinks.FindMissing(project, ProjectFolder));

        var newPath = CreateFile(Path.Combine(ProjectFolder, "clips", "clip.mp4"));
        var relinked = AssetLinks.Relink(project, missing.Id, newPath, ProjectFolder).Apply(project).Project;

        Assert.Equal(missing with { Path = "clips/clip.mp4" }, relinked.Assets[missing.Id]);
        Assert.Empty(AssetLinks.FindMissing(relinked, ProjectFolder));
    }

    [Fact]
    public void SuggestsFilesWithTheSameNameInAFolder()
    {
        var missing = new Asset(AssetId.New(), AssetKind.Video, "old/Clip.MP4");
        var project = ProjectWith(missing);
        var candidate = CreateFile(_folder.Combine("search", "deep", "clip.mp4"));
        CreateFile(_folder.Combine("search", "other.mp4"));

        var found = AssetLinks.FindByFileName(project, ProjectFolder, _folder.Combine("search"));

        Assert.Equal(Path.GetFullPath(candidate), found[missing.Id]);
        Assert.Empty(AssetLinks.FindByFileName(project, ProjectFolder, _folder.Combine("nowhere")));
    }
}
