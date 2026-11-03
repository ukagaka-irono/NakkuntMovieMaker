namespace NakkuntMovieMaker.Core.Serialization;

/// <summary>プロジェクトのファイルが読めなかった。Message は利用者に表示できる文</summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string file, string message, Exception? innerException = null)
        : base($"{file}: {message}", innerException)
    {
        File = file;
    }

    /// <summary>読めなかったファイル（プロジェクトのフォルダからの相対パス）</summary>
    public string File { get; }
}
