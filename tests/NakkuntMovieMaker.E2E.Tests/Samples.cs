using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.E2E.Tests;

/// <summary>samples/ のサンプルプロジェクト</summary>
internal static class Samples
{
    /// <summary>リポジトリの samples/ の下のフォルダ</summary>
    public static string Folder(string name) => Path.Combine(RepositoryRoot(), "samples", name);

    public static void RequireFfmpeg()
    {
        var loaded = FfmpegLibrary.TryLoad(out _, out var error);
        Assert.SkipWhen(!loaded && Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") != "1", $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    /// <summary>
    /// サンプルを一時フォルダに写す（書き出しでキャッシュを作るので、リポジトリの中を汚さない）。プロジェクトのファイルの場所を返す
    /// </summary>
    public static string CopyToTemp(string name, string temp)
    {
        var source = Folder(name);
        var target = Path.Combine(temp, name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(".nmm", StringComparison.Ordinal))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return Path.Combine(target, name + ".nmmproj");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NakkuntMovieMaker.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("リポジトリのフォルダが見つかりません");
    }
}
