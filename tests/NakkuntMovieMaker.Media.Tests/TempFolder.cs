namespace NakkuntMovieMaker.Media.Tests;

/// <summary>テスト用の一時フォルダ。破棄すると中身ごと消す</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nmm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>フォルダの中のすべてのファイル（一時ファイルを含む）</summary>
    public string[] AllFiles() => Directory.GetFiles(Path, "*", SearchOption.AllDirectories);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
